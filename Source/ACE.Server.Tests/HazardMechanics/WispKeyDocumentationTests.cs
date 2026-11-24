using System;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.HazardMechanics;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Every numeric key the wisps' Validate parses (WispRules.NumberKeys, beam_height included) is documented where an admin
    /// reads it: the hazard_mech_wisps property description and the /hazard wisps help block. Review F3 on 19d358426. Source
    /// text only; reads no PropertyManager value.
    /// </summary>
    [TestClass]
    public class WispKeyDocumentationTests
    {
        /// <summary>The key written as key=..., or as one name of a slash group (the description's "move/hold/burst/rest=" and "cx/cy=").</summary>
        private static bool Documents(string text, string key) => Regex.IsMatch(text, "(?<![a-z_])" + Regex.Escape(key) + "(=|/[a-z_/]+=)");

        private static string PropertyDescription()
            => PooledLootSourceText.Read("Source/ACE.Server/Managers/PropertyManager.cs").Split('\n')
                .Single(l => l.Contains("(\"hazard_mech_wisps\"", StringComparison.Ordinal));

        /// <summary>The /hazard help from the wisps heading to the next mechanic's.</summary>
        private static string HelpBlock()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/Command/Handlers/HazardCommands.cs");
            var start = text.IndexOf("  /hazard wisps [", StringComparison.Ordinal);
            var end = text.IndexOf("  /hazard memory", start + 1, StringComparison.Ordinal);

            Assert.IsTrue(start >= 0 && end > start, $"the wisps help block: start={start} end={end}");
            return text.Substring(start, end - start);
        }

        [TestMethod]
        public void EveryWispNumberKey_IsDocumented_InThePropertyDescription_AndTheHazardHelp()
        {
            var keys = WispRules.NumberKeys;
            CollectionAssert.Contains(keys, "beam_height");
            CollectionAssert.Contains(keys, "ceiling_beam");

            var description = PropertyDescription();
            var help = HelpBlock();

            var missing = keys.Where(k => !Documents(description, k)).Select(k => "property: " + k)
                .Concat(keys.Where(k => !Documents(help, k)).Select(k => "/hazard help: " + k)).ToList();

            Assert.AreEqual(0, missing.Count, string.Join("; ", missing));
        }

        [TestMethod]
        public void Control_TheCheckSeesAMissingKey()
        {
            Assert.IsFalse(Documents(HelpBlock().Replace("beam_height=", "beam_hight="), "beam_height"));
            Assert.IsTrue(Documents("x beam_height=<m> y", "beam_height"));
            Assert.IsTrue(Documents("x cx/cy=<m> y", "cx"));
            Assert.IsFalse(Documents("x beam_height (no value) y", "beam_height"), "a bare mention is not a documented key");
            Assert.IsFalse(Documents("x ceiling_beam_height=<m> y", "beam_height"), "a longer key does not count");
        }
    }
}
