using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Factories;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The WorldObjectFactory opt-in for the Avatar fighter (PropertyBool.AllegianceGauntletFighter, 9102), in both factory
    /// arms (weenie and in-memory biota), keyed on the bool and not on WeenieType. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class GauntletFactoryTests
    {
        [ClassInitialize]
        public static void Init(TestContext _) => GauntletFighterFixtures.EnsureInitialized();

        [TestMethod]
        public void Ids_AreTheReservedOnes()
        {
            Assert.AreEqual(9102, (int)PropertyBool.AllegianceGauntletFighter);
            Assert.AreEqual(1008504u, GauntletAvatarFighter.FighterWcid);
        }

        [TestMethod]
        public void FighterBool_BuildsFighter_WithoutBool_PlainCreature()
        {
            // the weenie arm
            var shaped = GauntletAvatarFighter.BuildFighterWeenie(GauntletFighterFixtures.Donor(), GauntletAvatarFighter.FighterName);
            Assert.IsTrue(shaped.PropertiesBool[PropertyBool.AllegianceGauntletFighter]);
            Assert.IsInstanceOfType(WorldObjectFactory.CreateWorldObject(shaped, GauntletFighterFixtures.NextGuid()), typeof(GauntletAvatarFighter));

            shaped.PropertiesBool.Remove(PropertyBool.AllegianceGauntletFighter);
            Assert.AreEqual(typeof(CombatPet), WorldObjectFactory.CreateWorldObject(shaped, GauntletFighterFixtures.NextGuid()).GetType(),
                "without the bool the type switch builds what it always did (a CombatPet weenie)");

            // keyed on the bool, not on WeenieType: a type-10 weenie with the bool is still the fighter
            var creatureType = GauntletAvatarFighter.BuildFighterWeenie(GauntletFighterFixtures.Donor(), null);
            creatureType.WeenieType = WeenieType.Creature;
            Assert.IsInstanceOfType(WorldObjectFactory.CreateWorldObject(creatureType, GauntletFighterFixtures.NextGuid()), typeof(GauntletAvatarFighter));

            creatureType.PropertiesBool.Remove(PropertyBool.AllegianceGauntletFighter);
            Assert.AreEqual(typeof(Creature), WorldObjectFactory.CreateWorldObject(creatureType, GauntletFighterFixtures.NextGuid()).GetType(),
                "a type-10 weenie without the bool is a plain Creature");
        }

        [TestMethod]
        public void BiotaArm_FighterBool_BuildsFighter_WithoutBool_Not()
        {
            var weenie = GauntletAvatarFighter.BuildFighterWeenie(GauntletFighterFixtures.Donor(), null);
            var biota = ACE.Entity.Adapter.WeenieConverter.ConvertToBiota(weenie, GauntletFighterFixtures.NextGuid().Full);

            // As in AvatarFactoryTests: the biota constructor chain loads inventory through DatabaseManager.Shard, which this
            // host does not have, so construction cannot finish. The constructor the factory entered is on the stack.
            StringAssert.Contains(ConstructorEntered(biota), "GauntletAvatarFighter..ctor", "a biota carrying 9102 must come back as the fighter");

            var plain = ACE.Entity.Adapter.WeenieConverter.ConvertToBiota(weenie, GauntletFighterFixtures.NextGuid().Full);
            plain.PropertiesBool.Remove(PropertyBool.AllegianceGauntletFighter);

            var control = ConstructorEntered(plain);
            Assert.IsFalse(control.Contains("GauntletAvatarFighter..ctor"), "control: without 9102 it is not the fighter");
            StringAssert.Contains(control, "CombatPet..ctor", "control: it takes the CombatPet arm");
        }

        [TestMethod]
        public void Factory_BothArmsKeyOnTheFlag_AheadOfTheTypeSwitch()
        {
            var code = PooledLootSourceText.Read("Source/ACE.Server/Factories/WorldObjectFactory.cs");

            var weenieCheck = code.IndexOf("if (weenie.GetProperty(PropertyBool.AllegianceGauntletFighter) == true)\n                return new ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter(weenie, guid);", StringComparison.Ordinal);
            var weenieSwitch = code.IndexOf("switch (objWeenieType)", StringComparison.Ordinal);
            var biotaCheck = code.IndexOf("biota.PropertiesBool.TryGetValue(PropertyBool.AllegianceGauntletFighter, out var isGauntletFighter) && isGauntletFighter)\n                return new ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter(biota);", StringComparison.Ordinal);
            var biotaSwitch = code.IndexOf("switch (biota.WeenieType)", StringComparison.Ordinal);

            Assert.IsTrue(weenieCheck > 0 && weenieCheck < weenieSwitch, $"the weenie arm must test 9102 before its type switch (check={weenieCheck}, switch={weenieSwitch})");
            Assert.IsTrue(biotaCheck > 0 && biotaCheck < biotaSwitch, $"the biota arm must test 9102 before its type switch (check={biotaCheck}, switch={biotaSwitch})");
        }

        private static string ConstructorEntered(Biota biota)
        {
            try
            {
                var wo = WorldObjectFactory.CreateWorldObject(biota);
                return wo == null ? "" : $"{wo.GetType().Name}..ctor (completed)";
            }
            catch (Exception ex)
            {
                return ex.StackTrace ?? "";
            }
        }

        [TestMethod]
        public void BuildFighterWeenie_IsIdempotent_AndKeepsTheDonorUntouched()
        {
            var donor = GauntletFighterFixtures.Donor(withCreateList: true);
            var once = GauntletAvatarFighter.BuildFighterWeenie(donor, GauntletAvatarFighter.FighterName);
            var twice = GauntletAvatarFighter.BuildFighterWeenie(once, null);

            CollectionAssert.AreEquivalent(new List<KeyValuePair<PropertyBool, bool>>(once.PropertiesBool), new List<KeyValuePair<PropertyBool, bool>>(twice.PropertiesBool));
            CollectionAssert.AreEquivalent(new List<KeyValuePair<PropertyInt, int>>(once.PropertiesInt), new List<KeyValuePair<PropertyInt, int>>(twice.PropertiesInt));
            Assert.AreEqual(once.PropertiesCreateList.Count, twice.PropertiesCreateList.Count);
            Assert.AreEqual(GauntletAvatarFighter.FighterName, twice.PropertiesString[PropertyString.Name], "a null name keeps the weenie's");

            Assert.AreEqual(43, donor.PropertiesInt[PropertyInt.Lifespan], "the donor (the cached weenie in production) is never written");
            Assert.IsFalse(donor.PropertiesBool.ContainsKey(PropertyBool.AllegianceGauntletFighter));
        }
    }
}
