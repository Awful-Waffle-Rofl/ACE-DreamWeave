using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.CombatSimulator;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.CharacterDetails.CharacterDetailsSourcePins;

namespace ACE.Server.Tests.CharacterDetails
{
    /// <summary>
    /// The crit-resist split: GetWeaponCriticalChance and GetWeaponMagicCritFrequency are now the composition of an
    /// attacker-side PreResist half and ApplyCritResist. The oracle here is the PRE-SPLIT arithmetic, written out:
    /// attacker-side rate times GetNegativeRatingMod(target crit resist rating), and the no-caster magic path a flat
    /// 0.05 that reads no rating at all.
    /// </summary>
    [TestClass]
    public class CharacterDetailsCritSplitTests
    {
        private const string WeaponFile = "ACE.Server/WorldObjects/WorldObject_Weapon.cs";

        private static uint nextGuid = 0x7D310000;
        private static uint nextWcid = 993100;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();
        }

        private static WorldObject Weapon(double critFrequency, WeenieType type = WeenieType.MeleeWeapon)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = type,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Crit Item" } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)(type == WeenieType.Caster ? ItemType.Caster : ItemType.MeleeWeapon) } },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.CriticalFrequency, critFrequency } },
            };

            return type == WeenieType.Caster ? new Caster(weenie, new ObjectGuid(nextGuid++)) : new MeleeWeapon(weenie, new ObjectGuid(nextGuid++));
        }

        private static readonly double[] Rates = { 0.0, 0.05, 0.1, 0.2, 0.37, 0.5, 0.9 };
        private static readonly int[] Ratings = { 0, 1, 5, 10, 25, 60, 200, 9999 };
        private static readonly int[] WielderCritRatings = { 0, 7, 30 };

        [TestMethod]
        public void ApplyCritResist_IsRateTimesTheNegativeRatingMod()
        {
            foreach (var rate in Rates)
                foreach (var rating in Ratings)
                    Assert.AreEqual((float)rate * Creature.GetNegativeRatingMod(rating), WorldObject.ApplyCritResist((float)rate, rating), $"rate {rate} rating {rating}");
        }

        [TestMethod]
        public void PhysicalCrit_Composed_EqualsPreSplitArithmetic_OverAGrid()
        {
            foreach (var rate in Rates)
                foreach (var rating in Ratings)
                    foreach (var wielderCrit in WielderCritRatings)
                    {
                        var weapon = Weapon(rate);
                        var wielder = TestCreatures.CreateAttacker();
                        wielder.SetProperty(PropertyInt.CritRating, wielderCrit);
                        var target = TestCreatures.CreateDefender();
                        target.SetProperty(PropertyInt.CritResistRating, rating);
                        var skill = wielder.GetCreatureSkill(Skill.UnarmedCombat);

                        var pre = WorldObject.GetWeaponCriticalChancePreResist(weapon, wielder, skill, target);
                        var expected = pre * Creature.GetNegativeRatingMod(rating);

                        Assert.AreEqual((float)rate + wielderCrit * 0.01f, pre, 1e-6f, "the attacker-side half reads the weapon and the wielder");
                        Assert.AreEqual(expected, WorldObject.GetWeaponCriticalChance(weapon, wielder, skill, target), $"rate {rate} rating {rating} wielder {wielderCrit}");
                        Assert.AreEqual(pre, WorldObject.GetWeaponCriticalChancePreResist(weapon, wielder, skill, null), "a null target changes nothing on the attacker side");
                    }
        }

        /// <summary>
        /// A Critical Strike imbue scales with the skill handed in (GetCriticalStrikeMod), so an off-hand read must pass the OFF weapon's
        /// own skill, not the main hand's: the two skills here give different chances from the same weapon and wielder.
        /// </summary>
        [TestMethod]
        public void CriticalStrike_ScalesWithTheSkillPassed_SoTheOffHandReadsItsOwnSkill()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Crit Strike Sword" } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.ImbuedEffect, (int)ImbuedEffectType.CriticalStrike },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.CriticalFrequency, 0.0 } },
            };
            var weapon = new MeleeWeapon(weenie, new ObjectGuid(nextGuid++));
            var wielder = TestCreatures.CreateAttacker();

            var mainSkill = wielder.GetCreatureSkill(Skill.Sword);
            mainSkill.InitLevel = 600;
            var offSkill = wielder.GetCreatureSkill(Skill.Axe);
            offSkill.InitLevel = 250;

            var main = WorldObject.GetWeaponCriticalChancePreResist(weapon, wielder, mainSkill, null);
            var off = WorldObject.GetWeaponCriticalChancePreResist(weapon, wielder, offSkill, null);

            Assert.IsTrue(main > off, $"positive control: the higher skill gives the higher Critical Strike chance ({main} vs {off})");
            Assert.IsTrue(off > 0.0f, "the off-hand skill still earns a chance");
        }

        [TestMethod]
        public void MagicCrit_Composed_EqualsPreSplitArithmetic_AndNoCasterStaysFlat()
        {
            foreach (var rate in Rates)
                foreach (var rating in Ratings)
                {
                    var wand = Weapon(rate, WeenieType.Caster);
                    var caster = TestCreatures.CreateAttacker();
                    var target = TestCreatures.CreateDefender();
                    target.SetProperty(PropertyInt.CritResistRating, rating);
                    var skill = caster.GetCreatureSkill(Skill.WarMagic);

                    var pre = WorldObject.GetWeaponMagicCritFrequencyPreResist(wand, caster, skill, target);
                    Assert.AreEqual(pre * Creature.GetNegativeRatingMod(rating), WorldObject.GetWeaponMagicCritFrequency(wand, caster, skill, target), $"rate {rate} rating {rating}");

                    // no caster: the flat 5% default, never mitigated, on both halves
                    Assert.AreEqual(0.05f, WorldObject.GetWeaponMagicCritFrequency(null, caster, skill, target), $"no wand, rating {rating}");
                    Assert.AreEqual(0.05f, WorldObject.GetWeaponMagicCritFrequencyPreResist(null, caster, skill, null));
                }
        }

        [TestMethod]
        public void AttackerTerms_WithoutADefender_IsThePreResistHalf()
        {
            var weapon = Weapon(0.2);
            var wielder = TestCreatures.CreateAttacker();
            wielder.SetProperty(PropertyInt.CritResistRating, 50);   // the old self-target divide-out had to cancel this
            var skill = wielder.GetCreatureSkill(Skill.UnarmedCombat);

            Assert.AreEqual(WorldObject.GetWeaponCriticalChancePreResist(weapon, wielder, skill, null), AttackerTerms.PhysicalCritChance(weapon, wielder, skill, null));

            var wand = Weapon(0.3, WeenieType.Caster);
            Assert.AreEqual(WorldObject.GetWeaponMagicCritFrequencyPreResist(wand, wielder, skill, null), AttackerTerms.MagicCritChance(wand, wielder, skill, null));
            Assert.AreEqual(0.05f, AttackerTerms.MagicCritChance(null, wielder, skill, null));
        }

        // ---- exact pins on the split (mutation-proven: see the PR report) ----

        [TestMethod]
        public void Pin_PhysicalCritIsTheComposition()
        {
            const string sig = "public static float GetWeaponCriticalChance(WorldObject weapon, Creature wielder, CreatureSkill skill, Creature target)";
            AssertLineOnce(WeaponFile, sig, "var critRate = GetWeaponCriticalChancePreResist(weapon, wielder, skill, target);");
            AssertLineOnce(WeaponFile, sig, "return ApplyCritResist(critRate, target.GetCritResistRating());");
            AssertNoLineContains(WeaponFile, "public static float GetWeaponCriticalChancePreResist(WorldObject weapon, Creature wielder, CreatureSkill skill, Creature target)",
                "GetCritResistRating");
        }

        [TestMethod]
        public void Pin_MagicCritIsTheComposition()
        {
            const string sig = "public static float GetWeaponMagicCritFrequency(WorldObject weapon, Creature wielder, CreatureSkill skill, Creature target)";
            AssertFirstIf(WeaponFile, sig, "if (weapon == null)");
            AssertLineOnce(WeaponFile, sig, "return WithPetFocusCriticalStrike(defaultMagicCritFrequency, defaultMagicCritFrequency, wielder, skill, target) + (float)SummonerFocus.PetFocusModValue(wielder, target, WeaponModId.Focus);");
            AssertLineOnce(WeaponFile, sig, "var critRate = GetWeaponMagicCritFrequencyPreResist(weapon, wielder, skill, target);");
            AssertLineOnce(WeaponFile, sig, "return ApplyCritResist(critRate, target.GetCritResistRating());");
            AssertNoLineContains(WeaponFile, "public static float GetWeaponMagicCritFrequencyPreResist(WorldObject weapon, Creature wielder, CreatureSkill skill, Creature target)",
                "GetCritResistRating");
        }

        [TestMethod]
        public void Pin_ApplyCritResist()
        {
            const string sig = "public static float ApplyCritResist(float critRate, int critResistRating)";
            AssertLineOnce(WeaponFile, sig, "var critResistRatingMod = Creature.GetNegativeRatingMod(critResistRating);");
            AssertLineOnce(WeaponFile, sig, "critRate *= critResistRatingMod;");
        }

        /// <summary>
        /// GetWeaponCritDamageMod's only target read is the PvP Crippling Blow lever, which classifies the pair first and
        /// treats a null target as not PvP - so a null target is safe there.
        /// </summary>
        [TestMethod]
        public void CritDamageMod_NullTarget_IsSafe()
        {
            var weapon = Weapon(0.1);
            weapon.SetProperty(PropertyInt.ImbuedEffect, (int)ImbuedEffectType.CripplingBlow);
            Assert.IsTrue(weapon.HasImbuedEffect(ImbuedEffectType.CripplingBlow), "positive control: the lever's branch is taken");

            var wielder = TestCreatures.CreateAttacker();
            var skill = wielder.GetCreatureSkill(Skill.UnarmedCombat);

            Assert.AreEqual(WorldObject.GetWeaponCritDamageMod(weapon, wielder, skill, TestCreatures.CreateDefender()),
                WorldObject.GetWeaponCritDamageMod(weapon, wielder, skill, null));
        }
    }
}
