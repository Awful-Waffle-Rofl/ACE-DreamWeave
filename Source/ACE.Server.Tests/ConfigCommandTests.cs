using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards the /config player command (gated behind the player_config_command tunable).
    ///
    /// Two defects are covered here. The first is the session killer: /config used to resend the full
    /// GameEventPlayerDescription snapshot, which the server otherwise only sends inside the login
    /// sequence, and that resend wedged the client until the session died of Network Timeout. That
    /// behaviour now sits behind a diagnostic tunable that must default to off - asserted below. Whether
    /// the client survives can only be proven live; what a unit test can pin is the default.
    ///
    /// The second is a plain typo: the GDLE-to-ACE name table mapped HearRoleplayChat onto a spelling
    /// that is not a CharacterOption member, and Enum.TryParse is case sensitive, so that setting was
    /// unreachable. The table is now checked against the names /config list advertises.
    ///
    /// Pure static logic - no session, no database, no world.
    /// </summary>
    [TestClass]
    public class ConfigCommandTests
    {
        /// <summary>
        /// Pulls the setting names out of the private configList that /config list prints, so this test
        /// keeps covering settings added later. Each entry is "Some category settings:\nNameA, NameB, ...".
        /// </summary>
        private static List<string> GetAdvertisedSettingNames()
        {
            var field = typeof(PlayerCommands).GetField("configList", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "PlayerCommands.configList is gone or was renamed; this test needs it.");

            var lines = (List<string>)field.GetValue(null);
            Assert.IsNotNull(lines);

            var names = new List<string>();

            foreach (var line in lines)
            {
                var parts = line.Split('\n');
                Assert.AreEqual(2, parts.Length, $"Unexpected configList entry shape: {line}");

                names.AddRange(parts[1].Split(',').Select(n => n.Trim()).Where(n => n.Length > 0));
            }

            return names;
        }

        [TestMethod]
        public void EverySettingConfigListAdvertises_ActuallyResolves()
        {
            var names = GetAdvertisedSettingNames();

            Assert.IsTrue(names.Count > 40, $"Expected the full option catalog, only parsed {names.Count} names.");

            var unresolved = names.Where(n => !PlayerCommands.TryResolveConfigOption(n, out _)).ToList();

            Assert.AreEqual(0, unresolved.Count,
                "These /config settings are advertised by /config list but do not resolve to a CharacterOption: " + string.Join(", ", unresolved));
        }

        [TestMethod]
        public void HearRoleplayChat_ResolvesToListenToRoleplayChat()
        {
            // Regression: the table used to say "ListentoRoleplayChat" (lowercase t in "to"), which is not
            // an enum member, so this one setting always answered "Unknown character option".
            Assert.IsTrue(PlayerCommands.TryResolveConfigOption("HearRoleplayChat", out var option));
            Assert.AreEqual(CharacterOption.ListenToRoleplayChat, option);
        }

        [TestMethod]
        public void SettingNameLookup_IsCaseInsensitive()
        {
            Assert.IsTrue(PlayerCommands.TryResolveConfigOption("showcloak", out var lower));
            Assert.IsTrue(PlayerCommands.TryResolveConfigOption("SHOWCLOAK", out var upper));

            Assert.AreEqual(CharacterOption.ShowYourCloak, lower);
            Assert.AreEqual(CharacterOption.ShowYourCloak, upper);
        }

        [TestMethod]
        public void UnknownOrEmptySettingNames_DoNotResolve()
        {
            Assert.IsFalse(PlayerCommands.TryResolveConfigOption("NotASetting", out _));
            Assert.IsFalse(PlayerCommands.TryResolveConfigOption("", out _));
            Assert.IsFalse(PlayerCommands.TryResolveConfigOption("   ", out _));
            Assert.IsFalse(PlayerCommands.TryResolveConfigOption(null, out _));

            // The table maps GDLE names, not ACE enum names, so the enum spelling must not sneak through.
            Assert.IsFalse(PlayerCommands.TryResolveConfigOption("ListenToRoleplayChat", out _));

            // Enum.TryParse accepts numbers; a bare number must not select an option by ordinal.
            Assert.IsFalse(PlayerCommands.TryResolveConfigOption("38", out _));
        }

        [TestMethod]
        public void ListRequest_IsRecognisedCaseInsensitively_AndOnlyForList()
        {
            Assert.IsTrue(PlayerCommands.IsConfigListRequest("list"));
            Assert.IsTrue(PlayerCommands.IsConfigListRequest("List"));
            Assert.IsTrue(PlayerCommands.IsConfigListRequest("LIST"));

            Assert.IsFalse(PlayerCommands.IsConfigListRequest("ShowCloak"));
            Assert.IsFalse(PlayerCommands.IsConfigListRequest("lists"));
            Assert.IsFalse(PlayerCommands.IsConfigListRequest(""));
        }

        [TestMethod]
        public void OnAndOff_AreAbsolute_RegardlessOfCurrentValue()
        {
            Assert.IsTrue(PlayerCommands.ResolveConfigValue(false, "on"));
            Assert.IsTrue(PlayerCommands.ResolveConfigValue(true, "on"));
            Assert.IsTrue(PlayerCommands.ResolveConfigValue(false, "ON"));
            Assert.IsTrue(PlayerCommands.ResolveConfigValue(false, "On"));

            Assert.IsFalse(PlayerCommands.ResolveConfigValue(true, "off"));
            Assert.IsFalse(PlayerCommands.ResolveConfigValue(false, "off"));
            Assert.IsFalse(PlayerCommands.ResolveConfigValue(true, "OFF"));
            Assert.IsFalse(PlayerCommands.ResolveConfigValue(true, "Off"));
        }

        [TestMethod]
        public void MissingOrUnrecognisedMode_Toggles()
        {
            // Retail-facing behaviour that must not drift: no mode argument means toggle, and anything the
            // handler does not recognise also toggles rather than being rejected.
            Assert.IsTrue(PlayerCommands.ResolveConfigValue(false, null));
            Assert.IsFalse(PlayerCommands.ResolveConfigValue(true, null));

            Assert.IsTrue(PlayerCommands.ResolveConfigValue(false, "toggle"));
            Assert.IsFalse(PlayerCommands.ResolveConfigValue(true, "toggle"));

            Assert.IsTrue(PlayerCommands.ResolveConfigValue(false, "yes"));
            Assert.IsFalse(PlayerCommands.ResolveConfigValue(true, "1"));
        }

        [TestMethod]
        public void PlayerDescriptionResendTunable_ExistsAndDefaultsToOff()
        {
            // This is the session-kill guard. If the default ever flips back to true, /config resends the
            // full player description mid session again and players get disconnected about a minute later.
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.TryGetValue("player_config_command_resend_description", out var property),
                "The player_config_command_resend_description tunable is missing.");

            Assert.IsFalse(property.Item,
                "player_config_command_resend_description must default to FALSE - it exists only to reproduce the /config disconnect.");
        }

        [TestMethod]
        public void ConfigCommandGate_StillDefaultsToOff()
        {
            // Unchanged by this fix, restated so a default flip is a test failure rather than a surprise.
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.TryGetValue("player_config_command", out var property));
            Assert.IsFalse(property.Item);
        }
    }
}
