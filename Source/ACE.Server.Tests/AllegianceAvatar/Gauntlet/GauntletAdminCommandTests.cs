using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Network;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// @gauntlet list | abort | census (plan Task 14): the pure parse, the list line, and the handler's routing through its
    /// seams (the run manager's list and abort, and the reply sink). No PropertyManager key is read; every seam is restored.
    /// </summary>
    [TestClass]
    public class GauntletAdminCommandTests
    {
        private List<string> replies;
        private List<(uint AvatarId, bool Refund)> aborts;

        [TestInitialize]
        public void Setup()
        {
            replies = new List<string>();
            aborts = new List<(uint, bool)>();
            GauntletAdminCommands.Output = (s, text) => replies.Add(text);
            GauntletAdminCommands.AbortRun = (id, refund) => { aborts.Add((id, refund)); return $"aborted {id} refund={refund}"; };
            GauntletAdminCommands.ListRuns = () => Array.Empty<GauntletRunInfo>();
        }

        [TestCleanup]
        public void Cleanup() => GauntletAdminCommands.ResetSeams();

        private static GauntletRunInfo Info(uint key = 7, uint avatar = 42, string refusal = null) =>
            new GauntletRunInfo(key, 900, avatar, "ow_arwic", 0x00010064, GauntletState.Active, 3, 250, 2, 118, 12, 9, refusal);

        // ---- parse ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Parse_List_Census_Abort()
        {
            Assert.AreEqual((GauntletAdminVerb.List, 0u, false, (string)null), GauntletAdminCommands.Parse(new[] { "LIST" }));
            Assert.AreEqual((GauntletAdminVerb.Census, 0u, false, (string)null), GauntletAdminCommands.Parse(new[] { "census" }));
            Assert.AreEqual((GauntletAdminVerb.Abort, 42u, false, (string)null), GauntletAdminCommands.Parse(new[] { "abort", "42" }));
            Assert.AreEqual((GauntletAdminVerb.Abort, 42u, true, (string)null), GauntletAdminCommands.Parse(new[] { "abort", "42", "REFUND" }));
        }

        [TestMethod]
        public void Parse_BadLines_AreUsage()
        {
            var bad = new[]
            {
                new string[0],
                new[] { "dance" },
                new[] { "list", "now" },
                new[] { "census", "now" },
                new[] { "abort" },
                new[] { "abort", "x" },
                new[] { "abort", "0" },
                new[] { "abort", "-3" },
                new[] { "abort", "42", "keep" },
                new[] { "abort", "42", "refund", "extra" },
            };

            foreach (var line in bad)
                Assert.AreEqual(GauntletAdminCommands.UsageText, GauntletAdminCommands.Parse(line).Error, string.Join(" ", line));
        }

        // ---- list line ------------------------------------------------------------------------------------------

        [TestMethod]
        public void ListLine_CarriesEveryAdminField()
        {
            var line = GauntletAdminCommands.ListLine(Info());

            foreach (var part in new[] { "run 7", "id 900", "Avatar 42", "ow_arwic", "Active", "wave 3 L250", "2 cleared", "118 s left", "12 fighting", "9 fought", "0x00010064" })
                StringAssert.Contains(line, part);

            Assert.IsFalse(line.Contains("last refusal"));
            StringAssert.Contains(GauntletAdminCommands.ListLine(Info(refusal: "no portals")), "[last refusal: no portals]");
        }

        // ---- handler routing --------------------------------------------------------------------------------------

        [TestMethod]
        public void Handler_List_PrintsOneLinePerRun_OrTheEmptyLine()
        {
            GauntletAdminCommands.HandleGauntlet(null, "list");
            CollectionAssert.AreEqual(new[] { "No Allegiance Gauntlet runs are live." }, replies);

            replies.Clear();
            GauntletAdminCommands.ListRuns = () => new[] { Info(1, 10), Info(2, 20) };
            GauntletAdminCommands.HandleGauntlet(null, "list");

            Assert.AreEqual(2, replies.Count);
            StringAssert.Contains(replies[0], "Avatar 10");
            StringAssert.Contains(replies[1], "Avatar 20");
        }

        [TestMethod]
        public void Abort_Refund_ReleasesPrice()
        {
            // The consumer pin: the handler hands the run manager the Avatar id and the refund flag the admin typed.
            GauntletAdminCommands.HandleGauntlet(null, "abort", "42", "refund");
            GauntletAdminCommands.HandleGauntlet(null, "abort", "43");

            CollectionAssert.AreEqual(new[] { (42u, true), (43u, false) }, aborts);
            CollectionAssert.AreEqual(new[] { "aborted 42 refund=True", "aborted 43 refund=False" }, replies);
        }

        [TestMethod]
        public void Handler_BadArguments_PrintUsage_AndAbortNothing()
        {
            GauntletAdminCommands.HandleGauntlet(null, "abort", "nope");
            GauntletAdminCommands.HandleGauntlet(null);

            Assert.AreEqual(0, aborts.Count);
            Assert.IsTrue(replies.All(r => r == GauntletAdminCommands.UsageText));
            Assert.AreEqual(2, replies.Count);
        }

        [TestMethod]
        public void Census_NeedsACharacter()
        {
            GauntletAdminCommands.HandleGauntlet(null, "census");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "needs a character");
        }

        // ---- the arena census verdict (folded in from the Task 5 spike) --------------------------------------------

        [TestMethod]
        public void Command_IsAdminOnly_AndTheSpikeIsGone()
        {
            var attr = typeof(GauntletAdminCommands).GetMethod(nameof(GauntletAdminCommands.HandleGauntlet))
                .GetCustomAttributes(typeof(CommandHandlerAttribute), false).Cast<CommandHandlerAttribute>().Single();

            Assert.AreEqual("gauntlet", attr.Command);
            Assert.AreEqual(ACE.Entity.Enum.AccessLevel.Admin, attr.Access);

            Assert.IsNull(typeof(GauntletAdminCommands).Assembly.GetType("ACE.Server.Command.Handlers.GauntletSpikeCommands"),
                "the developer spike command is removed");
        }
    }
}
