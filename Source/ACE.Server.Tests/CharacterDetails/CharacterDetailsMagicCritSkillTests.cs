using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.CombatSimulator;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.Tests.CharacterDetails
{
    /// <summary>
    /// The skill the Character Sheet sizes a held caster's Critical Strike / Crippling Blow imbue from in Magic stance
    /// (CharacterDetailsReader.PickMagicCritSkill). A Life caster used to be sized from War or Void, so a Hive Ichor Baton showed ~x2
    /// where a real Harm / Drain crit rolls on Life Magic.
    /// </summary>
    [TestClass]
    public class CharacterDetailsMagicCritSkillTests
    {
        private const float Epsilon = 0.0001f;

        private static uint nextGuid = 0x7D320000;
        private static uint nextWcid = 993200;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();
        }

        private static CreatureSkill MakeSkill(Skill skill, uint baseSkill)
            => new CreatureSkill(null, skill, new PropertiesSkill { InitLevel = baseSkill, SAC = SkillAdvancementClass.Inactive });

        private static Skill Pick(bool focus, Skill? wield, uint war, uint voidMagic, uint life)
            => CharacterDetailsReader.PickMagicCritSkill(focus, wield.HasValue ? (int)wield.Value : (int?)null, war, voidMagic, life);

        [TestMethod]
        public void SummonerFocus_ReadsSummoning_EvenWithAWieldSkillAndHigherMagic()
        {
            Assert.AreEqual(Skill.Summoning, Pick(true, null, 400, 300, 200));
            Assert.AreEqual(Skill.Summoning, Pick(true, Skill.LifeMagic, 100, 100, 400));
        }

        [TestMethod]
        public void WieldSkill_WinsOverAHigherOtherMagicSkill()
        {
            Assert.AreEqual(Skill.LifeMagic, Pick(false, Skill.LifeMagic, 400, 300, 100));
            Assert.AreEqual(Skill.VoidMagic, Pick(false, Skill.VoidMagic, 400, 100, 300));
            Assert.AreEqual(Skill.WarMagic, Pick(false, Skill.WarMagic, 100, 400, 300));
        }

        [TestMethod]
        public void NoWand_OrAnUnrelatedWieldSkill_ReadsTheHighestOfWarVoidLife()
        {
            foreach (Skill? wield in new Skill?[] { null, Skill.HeavyWeapons, Skill.Summoning, Skill.None })
            {
                Assert.AreEqual(Skill.WarMagic, Pick(false, wield, 300, 200, 100), "war highest");
                Assert.AreEqual(Skill.VoidMagic, Pick(false, wield, 100, 300, 200), "void highest");
                Assert.AreEqual(Skill.LifeMagic, Pick(false, wield, 100, 200, 300), "life highest");
            }
        }

        [TestMethod]
        public void Ties_PreferWarThenVoidThenLife()
        {
            Assert.AreEqual(Skill.WarMagic, Pick(false, null, 300, 300, 100), "war = void");
            Assert.AreEqual(Skill.WarMagic, Pick(false, null, 300, 300, 300), "all equal");
            Assert.AreEqual(Skill.WarMagic, Pick(false, null, 300, 100, 300), "war = life");
            Assert.AreEqual(Skill.VoidMagic, Pick(false, null, 100, 300, 300), "void = life");
            Assert.AreEqual(Skill.WarMagic, Pick(false, null, 0, 0, 0), "untrained");
        }

        [TestMethod]
        public void LifeWieldCripplingBlowCaster_ProjectsTheLifeSkillCrit_NotTheWarOne()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Caster,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Life Crit Baton" } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Caster },
                    { PropertyInt.WieldSkillType, (int)Skill.LifeMagic },
                    { PropertyInt.ImbuedEffect, (int)ImbuedEffectType.CripplingBlow },
                },
            };
            var wand = new Caster(weenie, new ObjectGuid(nextGuid++));
            var caster = TestCreatures.CreateAttacker();

            // (war, life, expected sheet mod): Life far above War is the reported bug; War above Life proves the wield skill decides
            foreach (var (war, life, expected, wrong) in new[] { (120u, 360u, 6.0f, 2.0f), (360u, 120u, 2.0f, 6.0f) })
            {
                var skills = new Dictionary<Skill, CreatureSkill>
                {
                    { Skill.WarMagic, MakeSkill(Skill.WarMagic, war) },
                    { Skill.VoidMagic, MakeSkill(Skill.VoidMagic, 0) },
                    { Skill.LifeMagic, MakeSkill(Skill.LifeMagic, life) },
                };

                var picked = CharacterDetailsReader.PickMagicCritSkill(false, wand.WieldSkillType, war, 0, life);
                var mod = AttackerTerms.MagicCritDamageMod(wand, caster, skills[picked], null);

                Assert.AreEqual(Skill.LifeMagic, picked, $"war {war} life {life}");
                Assert.AreEqual(expected, mod, Epsilon, $"war {war} life {life}: the Life-skill crit");
                Assert.AreEqual(wrong, AttackerTerms.MagicCritDamageMod(wand, caster, skills[Skill.WarMagic], null), Epsilon,
                    "positive control: sized from War it would show the other value");
            }
        }
    }
}
