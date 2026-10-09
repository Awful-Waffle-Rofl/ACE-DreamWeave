using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The PK facet rank refund (Docs/Facets/DESIGN.md section 9a). Ranks learned on the PK facet while the
    /// facet PK rules were off are erased when the player leaves it with the rules on, and their cost is
    /// refunded through the outgoing side of the class ability point delta.
    ///
    /// What this reaches: Player.PlanFacetAbilitySwap, the pure planner TrySwitchFacet applies, priced by the
    /// real ClassAbilityPointsSpent against the real ClassAbilityRegistry, and the chat line composer.
    /// What it does NOT reach (ACE.Server.Tests has no database and cannot construct a live Player):
    /// TrySwitchFacet itself, ApplyFacetAbilities' erase of the quest rows, AdjustClassAbilityPoints and its
    /// CAP ledger row, the chat send, and the biota/character saves. "Ranks gone" below means the planned
    /// incoming set (the only set ApplyFacetAbilities keeps) and the planned stored PK row hold none of them.
    ///
    /// Nothing here reads PropertyManager.
    /// </summary>
    [TestClass]
    public class FacetPkRankRefundTests
    {
        private const int NumberedSlot = 2;

        private static (ClassAbilityDefinition A, ClassAbilityDefinition B) TwoRankedAbilities()
        {
            var picked = ClassAbilityRegistry.Abilities.Values
                .Where(d => d.MaxRank >= 2 && d.CumulativeCost(1) > 0)
                .OrderBy(d => d.Name, System.StringComparer.Ordinal)
                .Take(2)
                .ToList();

            Assert.AreEqual(2, picked.Count, "fixture: the registry needs two abilities with at least two paid ranks");
            return (picked[0], picked[1]);
        }

        private static Dictionary<string, int> PkFacetStrayRanks(out int cost)
        {
            var (a, b) = TwoRankedAbilities();

            cost = a.CumulativeCost(2) + b.CumulativeCost(1);
            Assert.IsTrue(cost > 0, "fixture: the stray ranks must cost something, or a missing refund is invisible");

            return new Dictionary<string, int> { [a.Name] = 2, [b.Name] = 1 };
        }

        [TestMethod]
        public void LeavingPkFacet_RulesOn_WithRanks_RefundsExactlyTheirCost_AndTheRanksAreGone()
        {
            var live = PkFacetStrayRanks(out var cost);

            var plan = Player.PlanFacetAbilitySwap(Player.PkFacetSlot, NumberedSlot, pkRulesActive: true,
                live, new Dictionary<string, int>(), currentAvailable: 17);

            Assert.AreEqual(cost, plan.PkFacetRankRefund, "the refund is what the switch's own pricing charges for those ranks");
            Assert.AreEqual(17 + cost, plan.NewAvailable, "the pool rises by exactly the refund");
            Assert.AreEqual(0, plan.Shortfall);

            Assert.IsTrue(plan.DiscardedOutgoing);
            Assert.AreEqual(0, plan.Outgoing.Count, "the PK facet's stored row is written empty");
            Assert.AreEqual(0, plan.OutgoingSpent, "the stored set commits nothing; the refund is carried separately");

            foreach (var name in live.Keys)
                Assert.IsFalse(plan.Incoming.ContainsKey(name), $"{name} must not survive onto the character");
        }

        [TestMethod]
        public void LeavingPkFacet_RefundIsInTheDelta_NotARecompute()
        {
            // The incoming build is paid out of the same delta the refund lands in, and a pool below what a
            // lifetime recompute would give (a vendor spend) stays below it.
            var live = PkFacetStrayRanks(out var cost);
            var (a, _) = TwoRankedAbilities();

            var incomingRow = new Dictionary<string, int> { [a.Name] = 1 };

            var plan = Player.PlanFacetAbilitySwap(Player.PkFacetSlot, NumberedSlot, true, live, incomingRow, currentAvailable: 0);

            Assert.AreEqual(a.CumulativeCost(1), plan.IncomingSpent);
            Assert.AreEqual(cost - a.CumulativeCost(1), plan.NewAvailable, "current + refund - incoming, and nothing else");
            Assert.AreSame(incomingRow, plan.Incoming);
        }

        [TestMethod]
        public void SecondPass_RefundsNothing()
        {
            var live = PkFacetStrayRanks(out var cost);

            // Pass 1: leave the PK facet carrying the stray ranks.
            var leave1 = Player.PlanFacetAbilitySwap(Player.PkFacetSlot, NumberedSlot, true,
                live, new Dictionary<string, int>(), currentAvailable: 5);

            Assert.AreEqual(5 + cost, leave1.NewAvailable);

            // Back onto the PK facet: the live set is what pass 1 applied, and the PK row is what pass 1 stored.
            var enter = Player.PlanFacetAbilitySwap(NumberedSlot, Player.PkFacetSlot, true,
                leave1.Incoming, leave1.Outgoing, leave1.NewAvailable);

            Assert.AreEqual(0, enter.PkFacetRankRefund);
            Assert.AreEqual(leave1.NewAvailable, enter.NewAvailable);

            // Pass 2: leave again. Nothing is left to price.
            var leave2 = Player.PlanFacetAbilitySwap(Player.PkFacetSlot, NumberedSlot, true,
                enter.Incoming, enter.Outgoing, enter.NewAvailable);

            Assert.AreEqual(0, leave2.PkFacetRankRefund, "a second pass must refund nothing");
            Assert.AreEqual(leave1.NewAvailable, leave2.NewAvailable, "the pool must not move on the second pass");
            Assert.IsNull(Player.ComposePkFacetRankRefundLine(leave2.PkFacetRankRefund));
        }

        [TestMethod]
        public void NoRanks_NoChange_NoMessage()
        {
            var plan = Player.PlanFacetAbilitySwap(Player.PkFacetSlot, NumberedSlot, true,
                new Dictionary<string, int>(), new Dictionary<string, int>(), currentAvailable: 42);

            Assert.AreEqual(0, plan.PkFacetRankRefund);
            Assert.AreEqual(42, plan.NewAvailable);
            Assert.IsFalse(plan.DiscardedOutgoing);
            Assert.IsNull(Player.ComposePkFacetRankRefundLine(plan.PkFacetRankRefund), "no refund, no chat line");
        }

        [TestMethod]
        public void Control_RulesOff_LeavingPkFacet_ReleasesTheBuildOnce_NoRefund()
        {
            // With the rules off the PK facet is an ordinary slot: the ranks are priced and stored like any
            // slot's and released once, through the ordinary outgoing side. A refund on top would pay twice.
            var live = PkFacetStrayRanks(out var cost);

            var plan = Player.PlanFacetAbilitySwap(Player.PkFacetSlot, NumberedSlot, pkRulesActive: false,
                live, new Dictionary<string, int>(), currentAvailable: 17);

            Assert.AreEqual(0, plan.PkFacetRankRefund);
            Assert.AreEqual(cost, plan.OutgoingSpent);
            Assert.AreEqual(17 + cost, plan.NewAvailable, "released exactly once");
            Assert.AreSame(live, plan.Outgoing, "stored with the row, as any slot's build is");
        }

        [TestMethod]
        public void Control_EnteringPkFacet_WithAStoredRowOfRanks_RefundsNothing()
        {
            // A PK facet row written while the rules were off already released its points when it was
            // written. Refunding it again on entry would mint them.
            var stored = PkFacetStrayRanks(out _);

            var plan = Player.PlanFacetAbilitySwap(NumberedSlot, Player.PkFacetSlot, true,
                new Dictionary<string, int>(), stored, currentAvailable: 9);

            Assert.IsTrue(plan.DiscardedIncoming);
            Assert.AreEqual(0, plan.PkFacetRankRefund);
            Assert.AreEqual(0, plan.IncomingSpent);
            Assert.AreEqual(9, plan.NewAvailable);
        }

        [TestMethod]
        public void Control_NumberedToNumbered_WithRanks_RefundsNothing()
        {
            var live = PkFacetStrayRanks(out var cost);

            var plan = Player.PlanFacetAbilitySwap(1, NumberedSlot, true, live, new Dictionary<string, int>(), currentAvailable: 3);

            Assert.AreEqual(0, plan.PkFacetRankRefund);
            Assert.AreEqual(3 + cost, plan.NewAvailable, "an ordinary release, counted once");
        }

        [TestMethod]
        public void RefundLine_IsTheSuppliedTextVerbatim()
        {
            Assert.AreEqual(
                "Your class ability ranks from while the PK facet rules were off have been refunded: 37 points.",
                Player.ComposePkFacetRankRefundLine(37));

            Assert.IsNull(Player.ComposePkFacetRankRefundLine(0));
            Assert.IsNull(Player.ComposePkFacetRankRefundLine(-1));

            foreach (var c in Player.ComposePkFacetRankRefundLine(1234567))
                Assert.IsTrue(c >= 0x20 && c < 0x7F, $"player-facing text must be printable ASCII, found U+{(int)c:X4}");
        }
    }
}
