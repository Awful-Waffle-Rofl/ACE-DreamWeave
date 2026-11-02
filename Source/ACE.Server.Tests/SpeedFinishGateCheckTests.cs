using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Proving Grounds: Speed - the finish backstop's pure rule (Player.FindUnopenedRequiredGates): a run may
    /// only be scored when every objective-gated Door in its instance has latched open. No database, no world,
    /// no PropertyManager - hand-built gate snapshots only.
    /// </summary>
    [TestClass]
    public class SpeedFinishGateCheckTests
    {
        private static uint nextGuid = 0x70035F00;

        private static Player.SpeedGateSnapshot Door(string name, string key, bool latched, int required = 1)
            => Door(nextGuid++, name, key, latched, required);

        private static Player.SpeedGateSnapshot Door(uint guid, string name, string key, bool latched, int required = 1)
            => new Player.SpeedGateSnapshot(guid, true, required, key, latched, name);

        /// <summary>
        /// The Lost Coastal Archive's eight objective doors, with the two skipped in the 2026-09-18 prod run
        /// (D1 The Warded Stair, D6 The Choir Lock) left sealed. Everything else latched.
        /// </summary>
        private static List<Player.SpeedGateSnapshot> ArchiveWith(params string[] sealedNames)
        {
            var all = new[]
            {
                ("The Warded Stair", "lca_seal", 2),
                ("The Purge Lock", "lca_pentagon", 6),
                ("The Reliquary Gate", "lca_reliquary", 3),
                ("The Verger's Bar", "lca_verger", 1),
                ("The Bell Gate", "lca_bells", 3),
                ("The Choir Lock", "lca_choir", 2),
                ("The Ossuary Gate", "lca_ossuary", 1),
                ("The Archive Door", "lca_archive", 1),
            };

            return all.Select(g => Door(g.Item1, g.Item2, !sealedNames.Contains(g.Item1), g.Item3)).ToList();
        }

        [TestMethod]
        public void SkippedGates_AreRefused_AndNamed()
        {
            // Positive control: this is the shape of the run the backstop exists for. Without the check it
            // would have been scored.
            var unopened = Player.FindUnopenedRequiredGates(ArchiveWith("The Warded Stair", "The Choir Lock"), null);

            CollectionAssert.AreEquivalent(new[] { "The Warded Stair", "The Choir Lock" }, unopened);
        }

        [TestMethod]
        public void EveryGateLatched_IsAllowed()
        {
            Assert.AreEqual(0, Player.FindUnopenedRequiredGates(ArchiveWith(), null).Count);
        }

        [TestMethod]
        public void NoRequiredGates_IsAllowed()
        {
            // The test season's single-room course: no doors at all.
            Assert.AreEqual(0, Player.FindUnopenedRequiredGates(new List<Player.SpeedGateSnapshot>(), null).Count);
            Assert.AreEqual(0, Player.FindUnopenedRequiredGates(null, null).Count);
        }

        [TestMethod]
        public void NonDoorObjectiveGate_IsNotRequired()
        {
            var chest = new Player.SpeedGateSnapshot(0x70035F90, false, 3, "vault", false, "Sealed Chest");

            Assert.AreEqual(0, Player.FindUnopenedRequiredGates(new[] { chest }, null).Count);
        }

        [TestMethod]
        public void DoorWithoutObjectiveLock_IsNotRequired()
        {
            // Required 0 (not a gate) and a blank key (no contributor can ever reach it) are both ignored.
            var plainDoor = Door("Plain Door", "lca_x", false, required: 0);
            var keylessGate = Door("Keyless Gate", "  ", false, required: 2);

            Assert.AreEqual(0, Player.FindUnopenedRequiredGates(new[] { plainDoor, keylessGate }, null).Count);
        }

        [TestMethod]
        public void GateKeyedToTheFinishingSource_IsExempt_OthersStillCount()
        {
            // A boss that is both the objective and the Archive Door's contributor: its own contribution lands
            // after the finish hook, so that one gate must not refuse the run - but an unrelated sealed gate
            // still does.
            var gates = ArchiveWith("The Archive Door");
            Assert.AreEqual(0, Player.FindUnopenedRequiredGates(gates, "lca_archive").Count);

            var gatesWithSkip = ArchiveWith("The Archive Door", "The Warded Stair");
            CollectionAssert.AreEqual(new[] { "The Warded Stair" }, Player.FindUnopenedRequiredGates(gatesWithSkip, "lca_archive"));
        }

        [TestMethod]
        public void KeyMatch_IsOrdinal()
        {
            var gates = new[] { Door("The Archive Door", "lca_archive", false) };

            Assert.AreEqual(1, Player.FindUnopenedRequiredGates(gates, "LCA_ARCHIVE").Count);
        }

        [TestMethod]
        public void UnnamedGate_StillRefuses_WithAPlaceholderName()
        {
            var gates = new[] { Door(null, "k", false) };

            CollectionAssert.AreEqual(new[] { "an unnamed gate" }, Player.FindUnopenedRequiredGates(gates, null));
        }

        [TestMethod]
        public void DuplicateKey_LatchedWinner_UnlatchedTwin_IsAllowed_AndReported()
        {
            // Two gates share a key. Live routing sends every contribution to the lowest guid, so the twin can
            // never latch; refusing on it would refuse every run in the instance. Only the winner is checked,
            // and the duplicate is surfaced as a content warning, not a refusal.
            var winner = Door(0x70035F10, "The Bell Gate", "lca_bells", latched: true, required: 3);
            var twin = Door(0x70035F20, "The Bell Gate (twin)", "lca_bells", latched: false, required: 3);

            // Listing the twin first proves resolution is by guid, not by enumeration order.
            var check = Player.CheckRequiredGates(new[] { twin, winner }, null);

            Assert.AreEqual(0, check.Unopened.Count);
            CollectionAssert.AreEqual(new[] { "lca_bells" }, check.DuplicateKeys);
        }

        [TestMethod]
        public void DuplicateKey_UnlatchedWinner_IsRefused_NamingTheWinner()
        {
            // Control for the test above: the duplicate-key path still refuses when the gate that CAN latch
            // did not, and names that gate rather than the twin.
            var winner = Door(0x70035F10, "The Bell Gate", "lca_bells", latched: false, required: 3);
            var twin = Door(0x70035F20, "The Bell Gate (twin)", "lca_bells", latched: true, required: 3);

            var check = Player.CheckRequiredGates(new[] { twin, winner }, null);

            CollectionAssert.AreEqual(new[] { "The Bell Gate" }, check.Unopened);
            CollectionAssert.AreEqual(new[] { "lca_bells" }, check.DuplicateKeys);
        }

        [TestMethod]
        public void UniqueKeys_ReportNoDuplicates()
        {
            Assert.AreEqual(0, Player.CheckRequiredGates(ArchiveWith("The Warded Stair"), null).DuplicateKeys.Count);
        }

        [TestMethod]
        public void FinishingKeyExemption_CoversOnlyItsResolvedGate()
        {
            // The finishing source's key resolves to exactly one gate, and only that gate is exempt. A gate
            // sharing the finishing key is either that resolved gate or a non-winning twin that no
            // contribution can reach, so it is never checked either way; every gate on another key still
            // counts. Here the finishing key has a sealed winner (exempt: its contribution is in flight) and
            // a sealed twin (ignored, not separately exempted), while a sealed gate on a different key
            // refuses.
            var archiveDoor = Door(0x70035F10, "The Archive Door", "lca_archive", latched: false);
            var archiveTwin = Door(0x70035F20, "Archive Door Twin", "lca_archive", latched: false);
            var stair = Door(0x70035F30, "The Warded Stair", "lca_seal", latched: false, required: 2);

            var check = Player.CheckRequiredGates(new[] { archiveTwin, archiveDoor, stair }, "lca_archive");

            CollectionAssert.AreEqual(new[] { "The Warded Stair" }, check.Unopened);
            CollectionAssert.AreEqual(new[] { "lca_archive" }, check.DuplicateKeys);
        }

        /// <summary>
        /// Season 3, Cells of the Black Book: the start stub door (bb_stub, Required 1) and the Seal (bb_seal,
        /// Required 9) are objective-gate Doors; the three permanent hub doors are plain locked doors with no
        /// ObjectiveLockRequired. The boss and the Book carry no key.
        /// </summary>
        private static List<Player.SpeedGateSnapshot> BlackBook(bool stubLatched, bool sealLatched) => new List<Player.SpeedGateSnapshot>
        {
            Door(0x70089010, "Stub Door", "bb_stub", stubLatched, required: 1),
            Door(0x70089011, "The Seal", "bb_seal", sealLatched, required: 9),
            new Player.SpeedGateSnapshot(0x70089012, true, 0, null, false, "North Hub Door"),
            new Player.SpeedGateSnapshot(0x70089013, true, 0, null, false, "East Hub Door"),
            new Player.SpeedGateSnapshot(0x70089014, true, 0, null, false, "South Hub Door"),
        };

        [TestMethod]
        public void BlackBook_UnlatchedSeal_RefusesTheFinish()
        {
            CollectionAssert.AreEqual(new[] { "The Seal" }, Player.FindUnopenedRequiredGates(BlackBook(stubLatched: true, sealLatched: false), null));
            CollectionAssert.AreEquivalent(new[] { "Stub Door", "The Seal" }, Player.FindUnopenedRequiredGates(BlackBook(stubLatched: false, sealLatched: false), null));
        }

        [TestMethod]
        public void BlackBook_StubAndSealLatched_AllowsTheFinish_PermanentHubDoorsAreNotGates()
        {
            // The permanent hub doors stay locked for the whole run and must never refuse it.
            Assert.AreEqual(0, Player.FindUnopenedRequiredGates(BlackBook(stubLatched: true, sealLatched: true), null).Count);
        }

        /// <summary>
        /// The wings rework (2026-10-25): the open-order route clears three of the four corner rooms, so the fourth
        /// room's entry and exit doors (Required 1 each) stay unlatched at the Book. The course's optionalGateKeys
        /// exempt them; without the exemption the finish is refused (and the run forfeited) for a legitimate clear.
        /// </summary>
        [TestMethod]
        public void BlackBook_ASkippedCornerRoom_IsExemptByTheCoursesOptionalKeys_AndRefusedWithoutThem()
        {
            var gates = BlackBook(stubLatched: true, sealLatched: true);
            var guid = 0x70089F0Eu;

            foreach (var room in new[] { "nw", "sw", "ne", "se" })
            {
                var cleared = room != "se";
                gates.Add(Door(guid++, "Cloister Door", $"bb_{room}_in", cleared));
                gates.Add(Door(guid++, "Keeper's Door", $"bb_{room}_out", cleared));
            }

            var optional = new HashSet<string> { "bb_nw_in", "bb_nw_out", "bb_sw_in", "bb_sw_out", "bb_ne_in", "bb_ne_out", "bb_se_in", "bb_se_out" };

            Assert.AreEqual(0, Player.FindUnopenedRequiredGates(gates, null, optional).Count, "the skipped room is optional");
            CollectionAssert.AreEqual(new[] { "Cloister Door", "Keeper's Door" }, Player.FindUnopenedRequiredGates(gates, null), "control: without the exemption the skipped room refuses the finish");
            CollectionAssert.AreEqual(new[] { "Cloister Door", "Keeper's Door" }, Player.FindUnopenedRequiredGates(gates, null, new HashSet<string>()));

            // The exemption never covers a key it does not name: the Seal still refuses.
            var sealShut = BlackBook(stubLatched: true, sealLatched: false);
            CollectionAssert.AreEqual(new[] { "The Seal" }, Player.FindUnopenedRequiredGates(sealShut, null, optional));
        }

        [TestMethod]
        public void NonDoorWinner_ShadowsADoorTwin()
        {
            // Live routing does not filter on type: if a non-door gate holds the lowest guid for a key, it
            // absorbs every contribution and the door twin can never latch, so the door is not required.
            var chest = new Player.SpeedGateSnapshot(0x70035F10, false, 1, "vault", false, "Sealed Chest");
            var door = Door(0x70035F20, "Vault Door", "vault", latched: false);

            var check = Player.CheckRequiredGates(new[] { door, chest }, null);

            Assert.AreEqual(0, check.Unopened.Count);
            CollectionAssert.AreEqual(new[] { "vault" }, check.DuplicateKeys);
        }
    }
}
