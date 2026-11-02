using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Creature.TargetingTactic setter: a non-zero assignment must store the NEW value (it once re-wrote the getter's
    /// old value, making every non-zero assignment a no-op), and None removes the property.
    /// </summary>
    [TestClass]
    public class TargetingTacticSetterTests
    {
        private static uint nextGuid = 0x7F0002B1;

        [ClassInitialize]
        public static void Init(TestContext _) => TestGameTables.EnsureInitialized();

        private static Creature NewCreature()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 0x7F0002B1,
                ClassName = "targetingtacticsettertest",
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 10 },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Tactic Test" },
            };
            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void Assigning_a_nonzero_tactic_stores_the_new_value()
        {
            var c = NewCreature();
            Assert.AreEqual(TargetingTactic.None, c.TargetingTactic, "positive control: starts unset");

            c.TargetingTactic = TargetingTactic.Focused;
            Assert.AreEqual(TargetingTactic.Focused, c.TargetingTactic);
            Assert.AreEqual((int)TargetingTactic.Focused, c.GetProperty(PropertyInt.TargetingTactic));

            c.TargetingTactic = TargetingTactic.Random | TargetingTactic.Nearest;
            Assert.AreEqual(TargetingTactic.Random | TargetingTactic.Nearest, c.TargetingTactic);
        }

        [TestMethod]
        public void Assigning_None_removes_the_property()
        {
            var c = NewCreature();
            c.TargetingTactic = TargetingTactic.Weakest;
            Assert.IsNotNull(c.GetProperty(PropertyInt.TargetingTactic), "positive control: set first");

            c.TargetingTactic = TargetingTactic.None;

            Assert.IsNull(c.GetProperty(PropertyInt.TargetingTactic));
            Assert.AreEqual(TargetingTactic.None, c.TargetingTactic);
        }
    }
}
