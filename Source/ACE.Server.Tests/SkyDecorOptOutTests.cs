using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE fork: the per-player sky decor opt-out. The harness cannot build a Player (no session, no world), so these
    /// test the two factored pure functions the feature rests on: the /sky-decor access gate and the TrackObject send filter.
    /// Neither reads PropertyManager, so there is no static state to seed.
    /// </summary>
    [TestClass]
    public class SkyDecorOptOutTests
    {
        private static readonly string[] AdminSubcommands = { "info", "show", "set", "sql", "colours", "colors", "reload" };

        [TestMethod]
        public void Gate_Player_AllowedEnableDisableHelp()
        {
            foreach (var sub in new[] { "enable", "disable", "help", "ENABLE", "Disable" })
                Assert.IsTrue(SkyDecorCommands.IsSubcommandAllowed(sub, AccessLevel.Player), sub);
        }

        [TestMethod]
        public void Gate_Player_DeniedEveryAdminSubcommandAndUnknown()
        {
            foreach (var sub in AdminSubcommands)
                Assert.IsFalse(SkyDecorCommands.IsSubcommandAllowed(sub, AccessLevel.Player), sub);

            Assert.IsFalse(SkyDecorCommands.IsSubcommandAllowed("min_separation", AccessLevel.Player));
            Assert.IsFalse(SkyDecorCommands.IsSubcommandAllowed("", AccessLevel.Player));
            Assert.IsFalse(SkyDecorCommands.IsSubcommandAllowed(null, AccessLevel.Player));
        }

        [TestMethod]
        public void Gate_BelowDeveloper_StillDeniedAdminSubcommands()
        {
            foreach (var level in new[] { AccessLevel.Advocate, AccessLevel.Sentinel, AccessLevel.Envoy })
                foreach (var sub in AdminSubcommands)
                    Assert.IsFalse(SkyDecorCommands.IsSubcommandAllowed(sub, level), $"{level} {sub}");
        }

        [TestMethod]
        public void Gate_DeveloperAndAdmin_AllowedEverything()
        {
            foreach (var level in new[] { AccessLevel.Developer, AccessLevel.Admin })
            {
                foreach (var sub in AdminSubcommands)
                    Assert.IsTrue(SkyDecorCommands.IsSubcommandAllowed(sub, level), $"{level} {sub}");

                Assert.IsTrue(SkyDecorCommands.IsSubcommandAllowed("enable", level));
                Assert.IsTrue(SkyDecorCommands.IsSubcommandAllowed("disable", level));
            }
        }

        [TestMethod]
        public void Filter_SkyDecor_WithheldOnlyWhenHidden()
        {
            var decor = MakeObject(ObjectGuid.DynamicMin + 1);
            decor.IsSkyDecor = true;

            Assert.IsFalse(Player.ShouldSendToClient(decor, true), "decor must not be sent to an opted-out player");
            Assert.IsTrue(Player.ShouldSendToClient(decor, false), "decor is sent by default");
        }

        [TestMethod]
        public void Filter_NonDecor_StillSentWhenHidden_PositiveControl()
        {
            var ordinary = MakeObject(ObjectGuid.DynamicMin + 2);

            Assert.IsFalse(ordinary.IsSkyDecor);
            Assert.IsTrue(Player.ShouldSendToClient(ordinary, true), "a non-decor object is unaffected by the opt-out");
            Assert.IsTrue(Player.ShouldSendToClient(ordinary, false));
        }

        [TestMethod]
        public void PlayerFacingText_NamesNoAdminSubcommand_DeveloperTextNamesAll()
        {
            var attr = (CommandHandlerAttribute)Attribute.GetCustomAttribute(
                typeof(SkyDecorCommands).GetMethod("HandleSkyDecor"), typeof(CommandHandlerAttribute));

            Assert.IsNotNull(attr);
            Assert.AreEqual(AccessLevel.Player, attr.Access);

            var playerTexts = new[] { SkyDecorCommands.UsageText(AccessLevel.Player), attr.Description, attr.Usage };
            var adminWords = new[] { "info", "show", "set", "sql", "colours", "reload" };

            foreach (var text in playerTexts)
                foreach (var word in adminWords)
                    Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(text, @"\b" + word + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase), $"player text leaks '{word}': {text}");

            var dev = SkyDecorCommands.UsageText(AccessLevel.Developer);
            foreach (var word in adminWords)
                StringAssert.Contains(dev, word);

            StringAssert.Contains(SkyDecorCommands.UsageText(AccessLevel.Player), "enable");
            StringAssert.Contains(SkyDecorCommands.UsageText(AccessLevel.Player), "disable");
        }

        [TestMethod]
        public void Property_HideSkyDecor_IsPropertyBool9079()
        {
            Assert.AreEqual(9079, (int)PropertyBool.HideSkyDecor);
        }

        private static WorldObject MakeObject(uint guid)
        {
            var weenie = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 1002642,
                ClassName = "ace1002642-skyoptouttest",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Sky Rift" } },
            };

            return new GenericObject(weenie, new ObjectGuid(guid));
        }
    }
}