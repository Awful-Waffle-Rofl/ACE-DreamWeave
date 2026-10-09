using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The appraisal-panel lines a market snapshot carries. The fixtures are real fork weenies, so a
    /// line that would be blank in game is blank here too.
    /// </summary>
    [TestClass]
    public class MarketAppraisalTests
    {
        private static int nextGuid = 0x7F400000;

        private IClothingIconSource savedClothingIcons;

        [TestInitialize]
        public void Setup()
        {
            // No dats in a unit test; null means no ClothingBase override is possible, which is legal.
            savedClothingIcons = MarketSnapshot.ClothingIcons;
            MarketSnapshot.ClothingIcons = null;
        }

        [TestCleanup]
        public void Teardown() => MarketSnapshot.ClothingIcons = savedClothingIcons;

        /// <summary>
        /// Content/sql/weenies/1001904 Awfully OP Flaming Atlatl.sql, field for field. It carries
        /// PropertyInt.Damage 0, which is exactly why the old projection showed it nothing.
        /// </summary>
        private static Weenie FlamingAtlatl()
        {
            return new Weenie
            {
                WeenieClassId = 1001904,
                WeenieType = WeenieType.MissileLauncher,
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Awfully OP Flaming Atlatl" },
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MissileWeapon },
                    { PropertyInt.EncumbranceVal, 980 },
                    { PropertyInt.Value, 350 },
                    { PropertyInt.Damage, 0 },
                    { PropertyInt.DamageType, (int)DamageType.Fire },
                    { PropertyInt.WeaponSkill, (int)Skill.MissileWeapons },
                    { PropertyInt.WeaponTime, 14 },
                    { PropertyInt.AmmoType, (int)AmmoType.Atlatl },
                    { PropertyInt.CombatUse, (int)CombatUse.Missile },
                    { PropertyInt.ItemWorkmanship, 10 },
                    { PropertyInt.ItemCurMana, 6000 },
                    { PropertyInt.ItemMaxMana, 6000 },
                    { PropertyInt.ImbuedEffect, (int)ImbuedEffectType.FireRending },
                    { PropertyInt.ElementalDamageBonus, 22 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.ManaRate, -0.016666667 },
                    { PropertyFloat.MaximumVelocity, 27.3 },
                    { PropertyFloat.WeaponDefense, 1.2 },
                    { PropertyFloat.WeaponOffense, 1.0 },
                    { PropertyFloat.DamageMod, 2.6 },
                },
            };
        }

        /// <summary>Armour: an armour level plus the eight protection bands AppraiseInfo sends as ArmorProfile.</summary>
        private static Weenie BanedBreastplate()
        {
            return new Weenie
            {
                WeenieClassId = 1001872,
                WeenieType = WeenieType.Clothing,
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Awfully OP Breastplate" },
                    { PropertyString.LongDesc, "A breastplate of alarming quality." },
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                    { PropertyInt.ArmorLevel, 1200 },
                    { PropertyInt.MaterialType, (int)MaterialType.GreenGarnet },
                    { PropertyInt.ItemWorkmanship, 10 },
                    { PropertyInt.NumTimesTinkered, 4 },
                    { PropertyInt.EncumbranceVal, 1500 },
                    { PropertyInt.Value, 5000 },
                    { PropertyInt.WieldRequirements, (int)WieldRequirement.Level },
                    { PropertyInt.WieldDifficulty, 180 },
                    { PropertyInt.GearDamage, 5 },
                    { PropertyInt.EquipmentSetId, (int)EquipmentSet.Soldiers },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.ArmorModVsSlash, 1.75 },
                    { PropertyFloat.ArmorModVsPierce, 1.5 },
                    { PropertyFloat.ArmorModVsBludgeon, 1.25 },
                    { PropertyFloat.ArmorModVsFire, 0.8 },
                },
            };
        }

        /// <summary>A weapon whose wield requirement names a skill rather than a level.</summary>
        private static Weenie SkillGatedAxe()
        {
            return new Weenie
            {
                WeenieClassId = 1001883,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Awfully OP Heavy Axe" },
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.Damage, 80 },
                    { PropertyInt.DamageType, (int)DamageType.Slash },
                    { PropertyInt.WieldRequirements, (int)WieldRequirement.RawSkill },
                    { PropertyInt.WieldSkillType, (int)Skill.HeavyWeapons },
                    { PropertyInt.WieldDifficulty, 385 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.DamageVariance, 0.25 },
                },
            };
        }

        private static WorldObject AsItem(Weenie weenie)
            => new MissileLauncher(weenie, new ObjectGuid((uint)nextGuid++));

        private static List<string> PanelOf(Weenie weenie)
            => MarketSnapshot.FromWeenie(weenie).PanelLines;

        private static void AssertHas(IReadOnlyList<string> panel, string expected)
            => Assert.IsTrue(panel.Contains(expected),
                $"expected the panel line \"{expected}\"; the panel was:\n  {string.Join("\n  ", panel)}");

        [TestMethod]
        public void MissileLauncher_PanelShowsTheNumbersThatCarryItsDamage()
        {
            // The repo owner's complaint: an atlatl has no PropertyInt.Damage at all, so a projection
            // keyed on that field alone shows a weapon with no weapon stats.
            var panel = PanelOf(FlamingAtlatl());

            AssertHas(panel, "Damage Modifier: +160%");
            AssertHas(panel, "Elemental Damage Bonus: +22");
            AssertHas(panel, "Attack Bonus: +0%");
            AssertHas(panel, "Melee Defense Bonus: +20%");
            AssertHas(panel, "Damage Type: Fire");
            AssertHas(panel, "Speed: 14");
            AssertHas(panel, "Skill: Missile Weapons");
            AssertHas(panel, "Ammunition Type: Atlatl");
            AssertHas(panel, "Missile Velocity: 27.3");
            AssertHas(panel, "Imbued Effect: Fire Rending");
            AssertHas(panel, "Mana: 6000 / 6000");
            AssertHas(panel, "Workmanship: 10");
            AssertHas(panel, "Burden: 980");
            AssertHas(panel, "Value: 350");
        }

        [TestMethod]
        public void Armour_PanelShowsTheArmorLevelAndEveryProtectionBand()
        {
            var panel = PanelOf(BanedBreastplate());

            AssertHas(panel, "Armor Level: 1200");
            AssertHas(panel, "Slashing Protection: 1.75");
            AssertHas(panel, "Piercing Protection: 1.50");
            AssertHas(panel, "Bludgeoning Protection: 1.25");
            AssertHas(panel, "Fire Protection: 0.80");

            // AppraiseInfo sends the whole ArmorProfile, defaulting a band the item never set to 1.0.
            AssertHas(panel, "Cold Protection: 1.00");
            AssertHas(panel, "Acid Protection: 1.00");
            AssertHas(panel, "Nether Protection: 1.00");
            AssertHas(panel, "Lightning Protection: 1.00");

            AssertHas(panel, "Number of Times Tinkered: 4");
            AssertHas(panel, "Material: Green Garnet");
            AssertHas(panel, "Set: Soldiers");
            AssertHas(panel, "Damage Rating: +5");
        }

        [TestMethod]
        public void Armour_DescriptionTravelsWithTheSnapshot()
        {
            Assert.AreEqual("A breastplate of alarming quality.",
                MarketSnapshot.FromWeenie(BanedBreastplate()).LongDesc);
        }

        [TestMethod]
        public void WieldRequirement_NamesTheSkillItGatesOn()
        {
            AssertHas(PanelOf(SkillGatedAxe()), "Wield Requirement: Heavy Weapons 385");
        }

        [TestMethod]
        public void WieldRequirement_NamesTheLevelWhenThatIsWhatItGatesOn()
        {
            AssertHas(PanelOf(BanedBreastplate()), "Wield Requirement: Level 180");
        }

        [TestMethod]
        public void Weapon_PanelShowsTheDamageRangeFromItsVariance()
        {
            AssertHas(PanelOf(SkillGatedAxe()), "Damage: 60 - 80");
        }

        [TestMethod]
        public void Panel_NeverListsSpells_TheyHaveExactlyOneHomeInSpellNames()
        {
            // Spells rendered twice on the detail page: once from a panel line, once from the list.
            var weenie = FlamingAtlatl();
            weenie.PropertiesSpellBook = new Dictionary<int, float> { { 4325, 2f }, { 6107, 2f } };

            var snapshot = MarketSnapshot.FromWeenie(weenie);

            Assert.AreEqual(2, snapshot.SpellNames.Count, "the snapshot must still carry the spell names");
            Assert.IsFalse(snapshot.PanelLines.Any(l => l.StartsWith("Spell")),
                "a spell reached the panel lines as well as SpellNames:\n  " + string.Join("\n  ", snapshot.PanelLines));
        }

        [TestMethod]
        public void FromItem_AndFromWeenie_ProduceTheSamePanel()
        {
            // A vault ledger row and a listing of the same item must render identically.
            var weenie = FlamingAtlatl();

            var fromWeenie = MarketSnapshot.FromWeenie(weenie);
            var fromItem = MarketSnapshot.FromItem(AsItem(weenie));

            CollectionAssert.AreEqual(fromWeenie.PanelLines, fromItem.PanelLines,
                "the two projection paths drifted:\n  weenie: " + string.Join(" | ", fromWeenie.PanelLines)
                + "\n  item:   " + string.Join(" | ", fromItem.PanelLines));
        }

        [TestMethod]
        public void FromItem_AndFromWeenie_ProduceTheSameDescription()
        {
            var weenie = BanedBreastplate();

            Assert.AreEqual(MarketSnapshot.FromWeenie(weenie).LongDesc,
                            MarketSnapshot.FromItem(AsItem(weenie)).LongDesc);
        }
    }
}
