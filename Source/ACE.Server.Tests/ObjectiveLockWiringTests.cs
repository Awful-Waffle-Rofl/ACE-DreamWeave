using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Objective Locks - guards the pure decision logic that turns authored content properties into a
    /// contribution: which token key a contributor files under, what an unset weight means, how an expiry
    /// in seconds becomes an instant, and which object in a landblock is the gate.
    /// <para/>
    /// ObjectiveLock's own counting rules are covered by ObjectiveLockTests. This file covers the layer
    /// ABOVE it - the rules that decide what gets handed to Contribute in the first place - which is where
    /// a mis-authored dungeon actually goes wrong. Runs with no database, no dat file and no world:
    /// everything here goes through WorldObject's public static pure functions over hand-built values.
    /// </summary>
    [TestClass]
    public class ObjectiveLockWiringTests
    {
        private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }

        private static List<(uint Guid, string Key, int Required)> Candidates(params (uint Guid, string Key, int Required)[] candidates)
        {
            return new List<(uint Guid, string Key, int Required)>(candidates);
        }

        // =======================================================================================
        // ResolveObjectiveTokenKey
        // =======================================================================================

        [TestMethod]
        public void ResolveObjectiveTokenKey_AuthoredToken_IsUsedVerbatim()
        {
            Assert.AreEqual("bell_north", WorldObject.ResolveObjectiveTokenKey("bell_north", 0x7000001Au));
        }

        [TestMethod]
        public void ResolveObjectiveTokenKey_NoToken_FallsBackToTheContributorGuid()
        {
            // The load-bearing default: six creatures with no authored token are six distinct tokens.
            var a = WorldObject.ResolveObjectiveTokenKey(null, 0x7000001Au);
            var b = WorldObject.ResolveObjectiveTokenKey(null, 0x7000001Bu);

            Assert.AreNotEqual(a, b);
            Assert.AreEqual("guid:0x7000001A", a);
        }

        [TestMethod]
        public void ResolveObjectiveTokenKey_EmptyOrWhitespaceToken_FallsBackToTheContributorGuid()
        {
            // An empty PropertyString row is the shape a mis-authored token actually takes, and it must not
            // reach ObjectiveLock.Contribute - that throws on an empty key.
            Assert.AreEqual("guid:0x70000001", WorldObject.ResolveObjectiveTokenKey("", 0x70000001u));
            Assert.AreEqual("guid:0x70000001", WorldObject.ResolveObjectiveTokenKey("   ", 0x70000001u));
        }

        [TestMethod]
        public void ResolveObjectiveTokenKey_SharedAuthoredToken_CollapsesTwoContributorsIntoOne()
        {
            // The other half of the design: two objects that should collectively count once.
            var a = WorldObject.ResolveObjectiveTokenKey("lever_pair", 0x70000001u);
            var b = WorldObject.ResolveObjectiveTokenKey("lever_pair", 0x70000002u);

            Assert.AreEqual(a, b);
        }

        // =======================================================================================
        // ResolveObjectiveWeight
        // =======================================================================================

        [TestMethod]
        public void ResolveObjectiveWeight_Unset_DefaultsToOne()
        {
            Assert.AreEqual(1.0, WorldObject.ResolveObjectiveWeight(null));
        }

        [TestMethod]
        public void ResolveObjectiveWeight_Authored_IsUsedVerbatim()
        {
            Assert.AreEqual(3.0, WorldObject.ResolveObjectiveWeight(3));
        }

        [TestMethod]
        public void ResolveObjectiveWeight_AuthoredZero_StaysZeroRatherThanDefaulting()
        {
            // Distinct from unset on purpose - an explicit 0 is a deliberate "counts for nothing".
            Assert.AreEqual(0.0, WorldObject.ResolveObjectiveWeight(0));
        }

        // =======================================================================================
        // ResolveObjectiveExpiry
        // =======================================================================================

        [TestMethod]
        public void ResolveObjectiveExpiry_Unset_NeverExpires()
        {
            Assert.IsNull(WorldObject.ResolveObjectiveExpiry(null, Utc(2026, 9, 1)));
        }

        [TestMethod]
        public void ResolveObjectiveExpiry_Zero_NeverExpires()
        {
            // 0 is how content spells "no expiry" in a schema where numbers default to 0. Reading it as
            // "already expired" would make every such token dead on arrival.
            Assert.IsNull(WorldObject.ResolveObjectiveExpiry(0, Utc(2026, 9, 1)));
        }

        [TestMethod]
        public void ResolveObjectiveExpiry_Negative_NeverExpires()
        {
            Assert.IsNull(WorldObject.ResolveObjectiveExpiry(-5, Utc(2026, 9, 1)));
        }

        [TestMethod]
        public void ResolveObjectiveExpiry_PositiveSeconds_IsAddedToNow()
        {
            var now = Utc(2026, 9, 1, 12, 0, 0);

            Assert.AreEqual(Utc(2026, 9, 1, 12, 0, 10), WorldObject.ResolveObjectiveExpiry(10, now));
        }

        [TestMethod]
        public void ResolveObjectiveExpiry_FeedsAHoldThreeAtOnceGateCorrectly()
        {
            // End to end against the real counter: three levers with a 5 second expiry satisfy the gate
            // only while all three are live, which is the behaviour ObjectiveLockExpiry exists to express.
            var now = Utc(2026, 9, 1, 12, 0, 0);
            var objectiveLock = new ACE.Server.Entity.ObjectiveLock(3);

            Assert.IsFalse(objectiveLock.Contribute("lever_a", 1, now, WorldObject.ResolveObjectiveExpiry(5, now)));

            var t2 = now.AddSeconds(2);
            Assert.IsFalse(objectiveLock.Contribute("lever_b", 1, t2, WorldObject.ResolveObjectiveExpiry(5, t2)));

            // Lever A's token has expired by now, so the third pull only brings the live sum back to 2.
            var t6 = now.AddSeconds(6);
            Assert.IsFalse(objectiveLock.Contribute("lever_c", 1, t6, WorldObject.ResolveObjectiveExpiry(5, t6)));
            Assert.AreEqual(2.0, objectiveLock.CurrentWeight(t6));

            // Re-pull A inside the window and all three are live at once.
            Assert.IsTrue(objectiveLock.Contribute("lever_a", 1, t6, WorldObject.ResolveObjectiveExpiry(5, t6)));
        }

        // =======================================================================================
        // SelectObjectiveGateGuid
        // =======================================================================================

        [TestMethod]
        public void SelectObjectiveGateGuid_SingleMatchingGate_IsSelected()
        {
            var candidates = Candidates(
                (0x70000010u, "lca_pentagon", 3),
                (0x70000011u, "lca_pentagon", 0),      // a contributor - carries the key, no Required
                (0x70000012u, "lca_pentagon", 0));

            var gate = WorldObject.SelectObjectiveGateGuid(candidates, "lca_pentagon", out var ambiguous);

            Assert.AreEqual(0x70000010u, gate);
            Assert.IsFalse(ambiguous);
        }

        [TestMethod]
        public void SelectObjectiveGateGuid_ContributorsOnly_FindsNoGate()
        {
            var candidates = Candidates(
                (0x70000011u, "lca_pentagon", 0),
                (0x70000012u, "lca_pentagon", 0));

            Assert.IsNull(WorldObject.SelectObjectiveGateGuid(candidates, "lca_pentagon", out var ambiguous));
            Assert.IsFalse(ambiguous);
        }

        [TestMethod]
        public void SelectObjectiveGateGuid_GateForAnotherKey_IsNotSelected()
        {
            // Two independent puzzles in one landblock must not cross-wire.
            var candidates = Candidates(
                (0x70000010u, "lca_bells", 5),
                (0x70000020u, "lca_pentagon", 3));

            Assert.AreEqual(0x70000020u, WorldObject.SelectObjectiveGateGuid(candidates, "lca_pentagon", out _));
            Assert.AreEqual(0x70000010u, WorldObject.SelectObjectiveGateGuid(candidates, "lca_bells", out _));
        }

        [TestMethod]
        public void SelectObjectiveGateGuid_KeyComparisonIsCaseSensitive()
        {
            var candidates = Candidates((0x70000010u, "lca_pentagon", 3));

            Assert.IsNull(WorldObject.SelectObjectiveGateGuid(candidates, "LCA_Pentagon", out _));
        }

        [TestMethod]
        public void SelectObjectiveGateGuid_NegativeRequired_IsNotAGate()
        {
            var candidates = Candidates((0x70000010u, "lca_pentagon", -1));

            Assert.IsNull(WorldObject.SelectObjectiveGateGuid(candidates, "lca_pentagon", out _));
        }

        [TestMethod]
        public void SelectObjectiveGateGuid_TwoGatesOnOneKey_PicksLowestGuidAndReportsAmbiguous()
        {
            var candidates = Candidates(
                (0x70000030u, "lca_pentagon", 3),
                (0x70000010u, "lca_pentagon", 5),
                (0x70000020u, "lca_pentagon", 4));

            var gate = WorldObject.SelectObjectiveGateGuid(candidates, "lca_pentagon", out var ambiguous);

            Assert.AreEqual(0x70000010u, gate);
            Assert.IsTrue(ambiguous, "two gates sharing a key is a content error and must be reportable");
        }

        [TestMethod]
        public void SelectObjectiveGateGuid_TwoGatesOnOneKey_SelectionDoesNotDependOnCandidateOrder()
        {
            // The whole point of lowest-guid: broken content must break the SAME way every run, not
            // resolve to whichever object the landblock snapshot happened to list first.
            var forward = Candidates(
                (0x70000010u, "lca_pentagon", 3),
                (0x70000020u, "lca_pentagon", 3));

            var reversed = Candidates(
                (0x70000020u, "lca_pentagon", 3),
                (0x70000010u, "lca_pentagon", 3));

            Assert.AreEqual(
                WorldObject.SelectObjectiveGateGuid(forward, "lca_pentagon", out _),
                WorldObject.SelectObjectiveGateGuid(reversed, "lca_pentagon", out _));
        }

        [TestMethod]
        public void SelectObjectiveGateGuid_EmptyOrNullInputs_FindNoGate()
        {
            Assert.IsNull(WorldObject.SelectObjectiveGateGuid(null, "lca_pentagon", out _));
            Assert.IsNull(WorldObject.SelectObjectiveGateGuid(Candidates(), "lca_pentagon", out _));
            Assert.IsNull(WorldObject.SelectObjectiveGateGuid(Candidates((0x70000010u, "lca_pentagon", 3)), null, out _));
            Assert.IsNull(WorldObject.SelectObjectiveGateGuid(Candidates((0x70000010u, "lca_pentagon", 3)), "   ", out _));
        }
    }
}
