using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// summon-duel's Summoner support: the focus mods a pet reads (Precision, Execution, Bloodthirst) through the analytic
    /// spec against the ENGINE's own DamageEvent for the same ownerless pet and the same focus seam, and the --target-spells
    /// harness lever against what the defender profile actually receives.
    /// </summary>
    [TestClass]
    public class SummonDuelSupportTests
    {
        private static uint nextGuid = 0x7E300000;
        private static uint nextWcid = 996500;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();
        }

        /// <summary>Seeds every key these tests read and restores each from the defaults.</summary>
        private static void WithTunables(Action body)
        {
            PropertyManager.ModifyBool("weapon_mods_enabled", true);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0);

            try
            {
                body();
            }
            finally
            {
                PropertyManager.ModifyBool("weapon_mods_enabled", (bool)DefaultPropertyManager.DefaultBooleanProperties["weapon_mods_enabled"].Item);
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", DefaultPropertyManager.DefaultDoubleProperties["weapon_mod_magnitude_scale"].Item);
            }
        }

        /// <summary>An ownerless CombatPet (the duel's pet: no P_PetOwner) that always lands, with an unarmed slash hand.</summary>
        private static CombatPet OwnerlessPet(uint attackSkill = 400)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.CombatPet,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Overpower, 100 } },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    { PropertyAttribute.Strength, new PropertiesAttribute { InitLevel = 100 } },
                    { PropertyAttribute.Coordination, new PropertiesAttribute { InitLevel = 80 } },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    { Skill.UnarmedCombat, new PropertiesSkill { InitLevel = attackSkill, SAC = SkillAdvancementClass.Trained } },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    { CombatBodyPart.Hand, new PropertiesBodyPart { DType = DamageType.Slash, DVal = 40, DVar = 0.5f } },
                },
            };

            var pet = new CombatPet(weenie, new ObjectGuid(nextGuid++));
            pet.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            return pet;
        }

        private static double EngineMean(Creature attacker, Creature defender, int rolls)
        {
            var total = 0.0;

            for (var i = 0; i < rolls; i++)
                total += DamageEvent.CalculateDamage(attacker, defender, null).Damage;

            return total / rolls;
        }

        /// <summary>
        /// Step 1 of the support report: with Precision, Execution and Bloodthirst at full on the focus, the analytic mean the
        /// duel uses for a pet's melee hit equals the engine's DamageEvent mean for the same pet and the same seam, and the
        /// mods move it (positive control: the same pet with no focus is measurably lower). The pet carries no crit damage
        /// rating, so the analytic's unmodelled crit-branch rating recomposition is not in play.
        /// </summary>
        [TestMethod]
        public void FocusMods_ReachThePetsAnalyticHit_AtTheEnginesMagnitude()
        {
            WithTunables(() =>
            {
                var pet = OwnerlessPet();
                var defender = TestCreatures.CreateDefender(baseArmor: 50, maxHealth: 100000);
                var profile = MonsterDefenderProfile.Build(defender);

                var bare = MonsterCombatSimulator.Analytic(CreatureAttackerSpecs.Melee(pet, defender), profile).MeanDamagePerLandedHit;
                var bareEngine = EngineMean(pet, defender, 40000);

                var focus = SummonDuelBuilder.BuildSummonerFocus(1.0, SummonDuelBuilder.ParseSummonerMods("Focus=1,Execution=1,Bloodthirst=1"));

                using (SummonDuelBuilder.SimulateFocus(focus))
                {
                    var spec = CreatureAttackerSpecs.Melee(pet, defender);
                    var analytic = MonsterCombatSimulator.Analytic(spec, profile).MeanDamagePerLandedHit;
                    var engine = EngineMean(pet, defender, 40000);

                    Assert.AreEqual(engine, analytic, engine * 0.02, "the duel's analytic pet hit matches the engine with the focus mods on");
                    Assert.IsTrue(analytic > bare * 1.3, $"the mods must move the hit for this to prove anything (bare {bare}, focused {analytic})");
                    Assert.AreEqual(1.5f, spec.CritDamageMod / 2.0f, 1e-4f, "Execution at full is x1.5 on the 2.0 crit multiplier");
                }

                Assert.AreEqual(bareEngine, bare, bareEngine * 0.02, "positive control: no focus, still in parity");
            });
        }

        /// <summary>
        /// The live Crippling Blow transfer: a focus imbued with Crippling Blow, through the same seam, moves the analytic mean
        /// crit damage the duel uses for a pet's melee hit, and the ENGINE's DamageEvent mean for the same pet agrees within
        /// 2 %. The crit multiplier the spec carries is 1 + GetCripplingBlowMod(pet skill), the pet's own unarmed term being 1.
        /// </summary>
        [TestMethod]
        public void FocusCripplingBlow_ReachesThePetsAnalyticHit_AtTheEnginesMagnitude()
        {
            WithTunables(() =>
            {
                var pet = OwnerlessPet(attackSkill: 400);
                var defender = TestCreatures.CreateDefender(baseArmor: 50, maxHealth: 100000);
                var profile = MonsterDefenderProfile.Build(defender);

                var plain = SummonDuelBuilder.BuildSummonerFocus(1.0, SummonDuelBuilder.ParseSummonerMods("Focus=1"));
                var cb = SummonDuelBuilder.BuildSummonerFocus(1.0, SummonDuelBuilder.ParseSummonerMods("Focus=1"), ImbuedEffectType.CripplingBlow);

                double bare;
                using (SummonDuelBuilder.SimulateFocus(plain))
                    bare = MonsterCombatSimulator.Analytic(CreatureAttackerSpecs.Melee(pet, defender), profile).MeanDamagePerLandedHit;

                using (SummonDuelBuilder.SimulateFocus(cb))
                {
                    var spec = CreatureAttackerSpecs.Melee(pet, defender);
                    var skill = pet.GetCreatureSkill(pet.GetWeaponSkillForHit(CombatType.Melee));

                    Assert.AreEqual(1.0f + WorldObject.GetCripplingBlowMod(skill), spec.CritDamageMod, 1e-5f, "the spec carries the live Crippling Blow");
                    Assert.IsTrue(spec.CritDamageMod > 5.0f, $"the pet's skill sizes a real Crippling Blow (got {spec.CritDamageMod})");

                    var analytic = MonsterCombatSimulator.Analytic(spec, profile).MeanDamagePerLandedHit;
                    var engine = EngineMean(pet, defender, 40000);

                    Assert.AreEqual(engine, analytic, engine * 0.02, "the duel's analytic pet hit matches the engine with the focus Crippling Blow");
                    Assert.IsTrue(analytic > bare * 1.3, $"the imbue must move the hit (plain {bare}, imbued {analytic})");
                }
            });
        }

        /// <summary>
        /// The live Armor Rending transfer: against a heavily armored defender, an Armor Rending focus (through the seam) moves
        /// the analytic mean the duel uses for a pet's melee hit, the spec carries the helper DamageEvent reads, and the
        /// ENGINE's DamageEvent mean agrees within 2 %.
        /// </summary>
        [TestMethod]
        public void FocusArmorRending_ReachesThePetsAnalyticHit_AtTheEnginesMagnitude()
        {
            WithTunables(() =>
            {
                var pet = OwnerlessPet(attackSkill: 400);
                var defender = TestCreatures.CreateDefender(baseArmor: 400, maxHealth: 100000);
                var profile = MonsterDefenderProfile.Build(defender);

                double bare;
                using (SummonDuelBuilder.SimulateFocus(SummonDuelBuilder.BuildSummonerFocus(1.0, new (WeaponModId, double)[0])))
                    bare = MonsterCombatSimulator.Analytic(CreatureAttackerSpecs.Melee(pet, defender), profile).MeanDamagePerLandedHit;

                using (SummonDuelBuilder.SimulateFocus(SummonDuelBuilder.BuildSummonerFocus(1.0, new (WeaponModId, double)[0], ImbuedEffectType.ArmorRending)))
                {
                    var spec = CreatureAttackerSpecs.Melee(pet, defender);
                    var skill = pet.GetCreatureSkill(pet.GetWeaponSkillForHit(CombatType.Melee));

                    Assert.AreEqual(WorldObject.GetArmorRendingModFor(null, pet, skill, defender), spec.ArmorRendingMod, 1e-6f, "the spec carries the live helper");
                    Assert.IsTrue(spec.ArmorRendingMod < 0.5f, $"the pet's skill sizes a real Armor Rending (got {spec.ArmorRendingMod})");

                    var analytic = MonsterCombatSimulator.Analytic(spec, profile).MeanDamagePerLandedHit;
                    var engine = EngineMean(pet, defender, 40000);

                    Assert.AreEqual(engine, analytic, engine * 0.02, "the duel's analytic pet hit matches the engine with the focus Armor Rending");
                    Assert.IsTrue(analytic > bare * 1.2, $"the imbue must move the hit (plain {bare}, imbued {analytic})");
                }
            });
        }

        /// <summary>
        /// --target-spells: Imperil (-225 body armor, stat_Mod_Type 41088, the Incantation of Imperil Other row) and a x3.1 slash
        /// Vulnerability (stat_Mod_Type 20488 key 64, the Incantation of Blade Vulnerability Other row) put on a defender through ApplyTargetDebuff reach the profile the duel builds from it, and the
        /// analytic mean against that profile matches the ENGINE's DamageEvent mean against the same debuffed defender.
        /// </summary>
        [TestMethod]
        public void TargetDebuffs_ReachTheMonsterProfile_AndMatchTheEngine()
        {
            var attacker = TestCreatures.CreateAttacker(maxDamage: 40, variance: 0.5f);
            var clean = TestCreatures.CreateDefender(baseArmor: 300, maxHealth: 100000);
            var debuffed = TestCreatures.CreateDefender(baseArmor: 300, maxHealth: 100000);

            SummonDuelBuilder.ApplyTargetDebuff(debuffed, 4312, SpellCategory.ArmorValueLowering, 400,
                (EnchantmentTypeFlags)41088, 0, -225.0f);
            SummonDuelBuilder.ApplyTargetDebuff(debuffed, 4475, SpellCategory.SlashVulnerability, 400,
                (EnchantmentTypeFlags)20488, (uint)PropertyFloat.ResistSlash, 3.1f);

            var spec = CreatureAttackerSpecs.Melee(attacker);
            var cleanProfile = MonsterDefenderProfile.Build(clean);
            var profile = MonsterDefenderProfile.Build(debuffed);

            Assert.AreEqual(cleanProfile.EffectiveArmorLevel.Values.First() - 225.0f, profile.EffectiveArmorLevel[new MonsterArmorKey(CombatBodyPart.Chest, DamageType.Slash, false, false)], 1e-3f, "Imperil lowers the struck part's armor level");
            Assert.AreEqual(3.1f, profile.Resistance[DamageType.Slash].V0, 1e-5f, "the Vulnerability reaches the slash resistance row");
            Assert.AreEqual(1.0f, profile.Resistance[DamageType.Fire].V0, 1e-5f, "and no other element");

            var analytic = MonsterCombatSimulator.Analytic(spec, profile).MeanDamagePerLandedHit;
            var engine = EngineMean(attacker, debuffed, 40000);

            Assert.AreEqual(engine, analytic, engine * 0.02, "the debuffed analytic hit matches the engine");
            Assert.IsTrue(analytic > MonsterCombatSimulator.Analytic(spec, cleanProfile).MeanDamagePerLandedHit * 3.0, "the debuffs move the hit");

            Assert.ThrowsExactly<InvalidOperationException>(() => SummonDuelBuilder.ApplyTargetDebuff(debuffed, 4312, SpellCategory.ArmorValueLowering, 400, EnchantmentTypeFlags.BodyArmorValue, 0, -225.0f),
                "a second cast in a category needs the engine's stacking, which the lever does not model");
        }
    }
}
