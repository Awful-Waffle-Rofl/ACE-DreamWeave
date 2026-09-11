using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins <see cref="FacetCommands.ComposeHelp"/>, the /facet help text. The point of these tests is
    /// not the wording - it is that every VARIABLE part of the text comes from the same
    /// <see cref="FacetDials"/> the gates read, so help can never promise a threshold or a location the
    /// server is not enforcing. Each case therefore uses non-default dial values and asserts the stock
    /// defaults are absent, which a hardcoded string would fail.
    /// </summary>
    [TestClass]
    public class FacetHelpTests
    {
        private static FacetDials Dials(long slot2 = 300, long slot3 = 400, long slot4 = 500, string allowlistName = "the Marketplace", bool restricted = true)
        {
            var allowlist = restricted ? new HashSet<ushort> { 0x016C } : new HashSet<ushort>();

            return new FacetDials(true, slot2, slot3, slot4, allowlist, allowlistName);
        }

        [TestMethod]
        public void ComposeHelp_UnlockLevels_ComeFromTheDialsNotTheDefaults()
        {
            var help = FacetCommands.ComposeHelp(Dials(slot2: 120, slot3: 140, slot4: 160));

            StringAssert.Contains(help, "facet 2 at level 120");
            StringAssert.Contains(help, "facet 3 at level 140");
            StringAssert.Contains(help, "facet 4 at level 160");

            Assert.IsFalse(help.Contains("level 300"), "help must not restate the compiled default threshold");
        }

        [TestMethod]
        public void ComposeHelp_LocationLine_NamesTheAllowlistFromTheDials()
        {
            var help = FacetCommands.ComposeHelp(Dials(allowlistName: "Holtburg"));

            StringAssert.Contains(help, "You can only change facets in Holtburg.");
            Assert.IsFalse(help.Contains("Marketplace"), "the location must come from the dials, not a hardcoded name");
        }

        [TestMethod]
        public void ComposeHelp_EmptyAllowlist_DropsTheLocationLineEntirely()
        {
            // Count == 0 is "no allowlist configured", NOT "nothing is allowed" - Player.CheckFacetGates
            // lets every landblock through in that case, so help must not send the player anywhere.
            var help = FacetCommands.ComposeHelp(Dials(restricted: false));

            Assert.IsFalse(help.Contains("You can only change facets in"), "an unconfigured allowlist restricts nothing");
        }

        [TestMethod]
        public void ComposeHelp_ListsEveryFormOfTheCommandWithAWorkedExample()
        {
            var help = FacetCommands.ComposeHelp(Dials());

            StringAssert.Contains(help, "/facet <N>");
            StringAssert.Contains(help, "/facet name <N> <text>");
            StringAssert.Contains(help, "/facet help");

            StringAssert.Contains(help, "/facet 2");
            StringAssert.Contains(help, "/facet name 1 Void");
        }

        [TestMethod]
        public void ComposeHelp_SlotRange_TracksMaxFacetSlot()
        {
            var help = FacetCommands.ComposeHelp(Dials());

            StringAssert.Contains(help, $"turn to facet N (1-{Player.MaxFacetSlot})");
        }

        [TestMethod]
        public void ComposeHelp_SeparatesWhatAFacetKeepsFromWhatEveryFacetShares()
        {
            var help = FacetCommands.ComposeHelp(Dials());

            StringAssert.Contains(help, "Each facet keeps its own");
            StringAssert.Contains(help, "Every facet shares");
        }
    }
}
