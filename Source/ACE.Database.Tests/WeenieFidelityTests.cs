using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Adapter;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

namespace ACE.Database.Tests
{
    /// <summary>
    /// WeenieFidelity.FirstDifference, with no database: it must see order, leaf fields, nested emote actions and
    /// null-versus-empty, or the bulk loader's fidelity test would pass things it should not.
    /// </summary>
    [TestClass]
    public class WeenieFidelityTests
    {
        private static Weenie Sample()
        {
            var weenie = new Weenie { WeenieClassId = 10, ClassName = "sample", WeenieType = WeenieType.Creature };

            weenie.PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 5, [PropertyInt.Value] = 100 };
            weenie.PropertiesCreateList = new List<PropertiesCreateList>
            {
                new PropertiesCreateList { WeenieClassId = 1, DestinationType = DestinationType.Wield },
                new PropertiesCreateList { WeenieClassId = 2, DestinationType = DestinationType.Contain },
            };

            var emote = new PropertiesEmote { Category = EmoteCategory.Use, Probability = 1 };
            emote.PropertiesEmoteAction.Add(new PropertiesEmoteAction { Type = 1, Message = "hello" });
            weenie.PropertiesEmote = new List<PropertiesEmote> { emote };

            return weenie;
        }

        [TestMethod]
        public void IdenticalWeeniesHaveNoDifference()
        {
            Assert.IsNull(WeenieFidelity.FirstDifference(Sample(), Sample()));
            Assert.IsNull(WeenieFidelity.FirstDifference(null, null));
        }

        [TestMethod]
        public void DictionaryOrderIsADifference()
        {
            var b = Sample();
            b.PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Value] = 100, [PropertyInt.Level] = 5 };

            StringAssert.Contains(WeenieFidelity.FirstDifference(Sample(), b), "PropertiesInt[0]: key");
        }

        [TestMethod]
        public void ListOrderAndLeafFieldsAreDifferences()
        {
            var reordered = Sample();
            ((List<PropertiesCreateList>)reordered.PropertiesCreateList).Reverse();
            StringAssert.Contains(WeenieFidelity.FirstDifference(Sample(), reordered), "PropertiesCreateList[0]");

            var changed = Sample();
            ((List<PropertiesCreateList>)changed.PropertiesCreateList)[1].StackSize = 3;
            StringAssert.Contains(WeenieFidelity.FirstDifference(Sample(), changed), "PropertiesCreateList[1].StackSize");
        }

        [TestMethod]
        public void NestedEmoteActionsAreCompared()
        {
            var b = Sample();
            ((List<PropertiesEmote>)b.PropertiesEmote)[0].PropertiesEmoteAction[0].Message = "goodbye";

            StringAssert.Contains(WeenieFidelity.FirstDifference(Sample(), b), "PropertiesEmote[0].PropertiesEmoteAction[0].Message");
        }

        [TestMethod]
        public void NullVersusEmptyIsADifference()
        {
            var b = Sample();
            b.PropertiesSkill = new Dictionary<Skill, PropertiesSkill>();

            StringAssert.Contains(WeenieFidelity.FirstDifference(Sample(), b), "PropertiesSkill: null vs set");
        }
    }
}
