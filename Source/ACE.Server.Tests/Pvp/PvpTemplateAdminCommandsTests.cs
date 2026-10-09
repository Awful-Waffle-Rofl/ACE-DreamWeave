using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Server.Command.Handlers;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>The pure parts of /pvptemplate: the display-name sanitizer, the snapshot argument split, and the reply texts.</summary>
    [TestClass]
    public class PvpTemplateAdminCommandsTests
    {
        // ================= SanitizeDisplayName =================

        [TestMethod]
        public void SanitizeDisplayName_KeepsAPlainName()
        {
            Assert.AreEqual("War Mage (Tier 2)", PvpTemplateAdminCommands.SanitizeDisplayName("War Mage (Tier 2)", out var problem));
            Assert.IsNull(problem);
        }

        /// <summary>DISCRIMINATES the character filter: control characters, tags and chat-markup characters never survive.</summary>
        [TestMethod]
        public void SanitizeDisplayName_StripsControlAndFormattingCharacters()
        {
            Assert.AreEqual("Duelist", PvpTemplateAdminCommands.SanitizeDisplayName("Due\u0007list\u001b", out _));
            Assert.AreEqual("bDuelist/b", PvpTemplateAdminCommands.SanitizeDisplayName("<b>Duelist</b>", out _), "the angle brackets go; the letters and the plain slash stay");
            Assert.AreEqual("Duelist", PvpTemplateAdminCommands.SanitizeDisplayName("{Duelist}%\\@\"", out _));
        }

        [TestMethod]
        public void SanitizeDisplayName_CollapsesAndTrimsWhitespace()
        {
            Assert.AreEqual("War Mage", PvpTemplateAdminCommands.SanitizeDisplayName("  War \t\n  Mage  ", out _));
        }

        /// <summary>DISCRIMINATES the length cap: 64 is accepted, 65 is refused with the count in the problem.</summary>
        [TestMethod]
        public void SanitizeDisplayName_EnforcesTheColumnLength()
        {
            Assert.AreEqual(64, PvpTemplateAdminCommands.SanitizeDisplayName(new string('a', 64), out _).Length);

            Assert.IsNull(PvpTemplateAdminCommands.SanitizeDisplayName(new string('a', 65), out var problem));
            StringAssert.Contains(problem, "65");
        }

        [TestMethod]
        public void SanitizeDisplayName_NothingUsable_IsRefused()
        {
            Assert.IsNull(PvpTemplateAdminCommands.SanitizeDisplayName("<>{}\u0001", out var problem));
            Assert.IsNotNull(problem);
            Assert.IsNull(PvpTemplateAdminCommands.SanitizeDisplayName(null, out _));
        }

        // ================= TrySplitSnapshotArgs =================

        [TestMethod]
        public void SnapshotArgs_CharacterOnly_HasNoName()
        {
            Assert.IsTrue(PvpTemplateAdminCommands.TrySplitSnapshotArgs("Some Character", out var character, out var name));
            Assert.AreEqual("Some Character", character);
            Assert.IsNull(name);
        }

        [TestMethod]
        public void SnapshotArgs_BarSeparatesTheDisplayName()
        {
            Assert.IsTrue(PvpTemplateAdminCommands.TrySplitSnapshotArgs("Some Character | War Mage", out var character, out var name));
            Assert.AreEqual("Some Character", character);
            Assert.AreEqual("War Mage", name);
        }

        [TestMethod]
        public void SnapshotArgs_EmptySides_AreRefused()
        {
            Assert.IsFalse(PvpTemplateAdminCommands.TrySplitSnapshotArgs("Some Character |", out _, out _));
            Assert.IsFalse(PvpTemplateAdminCommands.TrySplitSnapshotArgs("| War Mage", out _, out _));
            Assert.IsFalse(PvpTemplateAdminCommands.TrySplitSnapshotArgs("  ", out _, out _));
        }

        // ================= texts =================

        [TestMethod]
        public void Usage_NamesTheNameSubcommand()
        {
            StringAssert.Contains(PvpTemplateAdminCommands.Usage, "name <key> <display name>");
            StringAssert.Contains(PvpTemplateAdminCommands.Usage, "[| <display name>]");
        }

        [TestMethod]
        public void WriteResultText_CoversEveryStatus()
        {
            Assert.AreEqual("[PvpTemplate] duelist is now shown as \"X\".", PvpTemplateAdminCommands.WriteResultText("duelist", PvpTemplateStoreStatus.Ok, "is now shown as \"X\""));
            StringAssert.Contains(PvpTemplateAdminCommands.WriteResultText("duelist", PvpTemplateStoreStatus.NotFound, "x"), "No template duelist");
            StringAssert.Contains(PvpTemplateAdminCommands.WriteResultText("duelist", PvpTemplateStoreStatus.Failed, "x"), "failed");
        }
    }
}
