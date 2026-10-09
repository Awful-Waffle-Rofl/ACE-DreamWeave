using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards CheckFacetGates' "something is open" refusal against the false positive fixed alongside this
    /// file: the repo owner closed a vendor panel, stayed standing at the vendor, and was still told to
    /// "Close whatever you have open before changing facets" - sometimes needing to reopen and reclose the
    /// vendor several times before a switch was allowed.
    ///
    /// THE MECHANISM, because the assertions only make sense against it. LastOpenedContainerId does not mean
    /// "a window is open". Vendor.ApproachVendor SETS it on approach, and the only thing that clears it is
    /// Vendor.CheckClose, on a 1.5s poll, once the player has moved outside UseRadius. Closing a vendor panel
    /// sends the server nothing at all - the client has no close message for a vendor - so for a vendor the
    /// flag really means "still standing here", and refusing on it is refusing on proximity. The reopen /
    /// reclose that appeared to clear it was really the player drifting out of radius and CheckClose firing.
    /// Real containers are the opposite case: Container.FinishClose clears the flag, so for those the flag
    /// does track an open view, and the gate must keep refusing.
    ///
    /// WHAT THESE CATCH is stated per test. They are source-shape assertions because ACE.Server.Tests has no
    /// database, no live Player and no Session, so CheckFacetGates cannot be executed here at all - the same
    /// limit FacetStripRoomTests states, and the same instrument FacetVaultRestoreOrderingTests,
    /// VaultCollapseTests and AccountVaultPurgeTests already use. BE CLEAR ABOUT THE LIMIT: they prove the
    /// gate is written to ask the object rather than the latch. Only the live check proves the refusal
    /// actually stops appearing, and that is queued.
    /// </summary>
    [TestClass]
    public class FacetVendorGateTests
    {
        private const string FacetsRelativePath = "Source/ACE.Server/WorldObjects/Player_Facets.cs";

        // MethodBody bounds a region by the NEXT member declaration rather than by a named successor, so no
        // guard here depends on the existence of any method other than the one it is about.
        private const string GatesSignature = "internal bool CheckFacetGates(";

        /// <summary>
        /// CONTROL for every source assertion below. This file's own doc comments name every symbol the
        /// assertions search for, so a stripper that stopped stripping would let prose satisfy them.
        /// </summary>
        [TestMethod]
        public void CommentStripper_ActuallyRemovesTheDocCommentsThatNameTheseSymbols()
        {
            var raw = File.ReadAllText(FindInSourceTree(FacetsRelativePath));
            var code = AccountVaultPurgeTests.StripComments(raw);

            StringAssert.Contains(raw, "/// <summary>", "Player_Facets.cs has no doc comments at all, so this control proves nothing - re-aim it.");
            Assert.IsFalse(code.Contains("/// <summary>"),
                "StripComments left doc comments in place, so every source assertion in this file can be satisfied by a comment mentioning the symbol rather than by code checking it.");
        }

        /// <summary>
        /// CATCHES the bug itself coming back. The pre-fix gate refused on a bare
        /// `LastOpenedContainerId != ObjectGuid.Invalid`, which is true for as long as the player stands
        /// within a vendor's UseRadius whether or not any panel is open.
        /// </summary>
        [TestMethod]
        public void Gate_DoesNotRefuseOnTheBareLatchComparison()
        {
            var body = MethodBody(GatesSignature);

            Assert.IsFalse(Regex.IsMatch(body, @"if\s*\(\s*LastOpenedContainerId\s*!=\s*ObjectGuid\.Invalid\s*\)"),
                "CheckFacetGates is refusing on a bare LastOpenedContainerId != ObjectGuid.Invalid again. That latch is set by Vendor.ApproachVendor and cleared only when the player walks out of UseRadius, so it means 'still standing at a vendor', never 'a window is open' - which is the false positive this guard exists to prevent.");
        }

        /// <summary>
        /// CATCHES the resolve-and-ask being replaced by anything that trusts the latch. The gate must fetch
        /// the object the id names and check the container's OWN open state, which is the triple check
        /// Container.ActOnUse already performs before closing a previously-opened container.
        /// </summary>
        [TestMethod]
        public void Gate_ResolvesTheObjectAndAsksWhetherItIsActuallyOpen()
        {
            var body = MethodBody(GatesSignature);

            StringAssert.Contains(body, "GetObject(LastOpenedContainerId)",
                "CheckFacetGates no longer resolves LastOpenedContainerId to an object. Without the resolve it cannot tell an open container from a vendor the player is merely standing next to.");
            StringAssert.Contains(body, "is Container",
                "CheckFacetGates no longer type-tests the resolved object. A Vendor is a Creature, not a Container, so the type test is what keeps a plain vendor from tripping the container refusal.");
            // Deliberately a regex over the whole condition, not two Contains. Both halves have to be
            // ANDed: `IsOpen || Viewer == Guid.Full` contains each substring and would refuse on any
            // container anyone had open, which is a fresh false-refusal class of exactly the kind this
            // fix exists to remove.
            Assert.IsTrue(Regex.IsMatch(body, @"\.IsOpen\s*&&\s*\w+\.Viewer\s*==\s*Guid\.Full"),
                "CheckFacetGates no longer requires BOTH that the container is IsOpen AND that this player is its Viewer. Container.FinishClose clears IsOpen and the latch together, so IsOpen is the half that tracks an open view; the Viewer half is what stops a container someone ELSE has open from refusing this player's switch. ORing them, or dropping either, reopens a false refusal.");
        }

        /// <summary>
        /// CATCHES the PersonalVendor carve-out being dropped as redundant. It is deliberately NOT justified
        /// by the latch: a PersonalVendor is a view onto the account vault, and the gear restore can withdraw
        /// from that vault mid-switch. A PersonalVendor is not a Container, so it falls straight through the
        /// check above and needs its own refusal until that interaction is traced.
        /// </summary>
        [TestMethod]
        public void Gate_StillRefusesWhileAPersonalVendorIsTheLastOpened()
        {
            var body = MethodBody(GatesSignature);

            // Anchored on the `if (` so a negated form cannot satisfy it: `if (!(lastOpened is
            // PersonalVendor))` contains the substring and would invert the carve-out into refusing on
            // everything EXCEPT a personal vendor.
            Assert.IsTrue(Regex.IsMatch(body, @"if\s*\(\s*lastOpened is PersonalVendor\s*\)"),
                "CheckFacetGates dropped or inverted the PersonalVendor refusal. That carve-out is not about the latch: a PersonalVendor is an account-vault view and the facet gear restore can withdraw from the vault mid-switch, so it stays until someone traces that interaction end to end and can remove it on evidence.");
        }

        private static string MethodBody(string startSignature)
        {
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(FindInSourceTree(FacetsRelativePath)));

            var start = code.IndexOf(startSignature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"'{startSignature}' was not found in Player_Facets.cs - has it been renamed? This guard must follow it, not silently scan nothing.");

            // Comments are already stripped, so the next class-indented access modifier is the next member.
            var next = Regex.Match(code.Substring(start + startSignature.Length), @"\n        (?:private|public|internal|protected)\s");

            Assert.IsTrue(next.Success, $"No member declaration follows '{startSignature}' in Player_Facets.cs, so this guard cannot bound the method body it is meant to read. Re-aim it rather than letting it scan the rest of the file.");

            return code.Substring(start, startSignature.Length + next.Index);
        }

        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");
            return null;
        }
    }
}
