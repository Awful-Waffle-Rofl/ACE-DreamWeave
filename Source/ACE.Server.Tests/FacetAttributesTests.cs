using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Facets;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The per-facet attribute conservation check. A facet stores the six primary attributes'
    /// InitLevel; the switch must write an arrangement whose sum equals the live character's, because
    /// the difference between those two sums is real innate points that would otherwise be silently
    /// created or destroyed.
    ///
    /// These are real behavioural tests, not source-shape ones: FacetAttributes takes no Player, no
    /// DatManager and no database, which is the whole reason it was split out that way.
    /// </summary>
    [TestClass]
    public class FacetAttributesTests
    {
        private static Dictionary<PropertyAttribute, uint> Arrangement(uint str, uint end, uint quick, uint coord, uint focus, uint self)
        {
            return new Dictionary<PropertyAttribute, uint>
            {
                { PropertyAttribute.Strength,     str },
                { PropertyAttribute.Endurance,    end },
                { PropertyAttribute.Quickness,    quick },
                { PropertyAttribute.Coordination, coord },
                { PropertyAttribute.Focus,        focus },
                { PropertyAttribute.Self,         self },
            };
        }

        /// <summary>
        /// CATCHES: a PrimaryAttributes list that has drifted out of PropertyAttribute enum order, or
        /// gained/lost an entry. Everything else in this class rests on it - the surplus tiebreak IS
        /// this order, and "six attribute sends" in the apply path is this length.
        /// </summary>
        [TestMethod]
        public void PrimaryAttributes_AreTheSixInEnumOrder()
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    PropertyAttribute.Strength,
                    PropertyAttribute.Endurance,
                    PropertyAttribute.Quickness,
                    PropertyAttribute.Coordination,
                    PropertyAttribute.Focus,
                    PropertyAttribute.Self,
                },
                FacetAttributes.PrimaryAttributes,
                "the tiebreak order is PropertyAttribute ENUM order (Quickness 3 before Coordination 4), not the display order the character sheet uses");
        }

        /// <summary>
        /// CATCHES: a Sum that folds in a key outside Strength..Self. A biota's PropertiesAttribute
        /// collection can carry those (verify-attributes exists to remove them), and one counted here
        /// would make the live and stored sums disagree for a reason unrelated to the player's
        /// redistribution - producing a bogus refusal or a bogus surplus.
        /// </summary>
        [TestMethod]
        public void Sum_CountsOnlyTheSixPrimaries()
        {
            var withStray = Arrangement(100, 100, 10, 100, 10, 10);
            withStray[PropertyAttribute.Undef] = 5000;

            Assert.AreEqual(330u, FacetAttributes.Sum(withStray));
            Assert.AreEqual(0u, FacetAttributes.Sum(null));
        }

        /// <summary>
        /// CATCHES: any reconciliation that rewrites an already-conserving arrangement. This is the
        /// ordinary case - the same character, the same innate total - and the stored arrangement must
        /// come back byte for byte, or every switch would slowly drift a player's build.
        /// </summary>
        [TestMethod]
        public void Reconcile_EqualSums_ReturnsTheStoredArrangementVerbatim()
        {
            var stored = Arrangement(100, 100, 10, 100, 10, 10);

            var result = FacetAttributes.Reconcile(stored, 330, out var surplus, out var shortfall);

            Assert.IsNotNull(result);
            Assert.AreEqual(0u, surplus);
            Assert.AreEqual(0u, shortfall);

            foreach (var attribute in FacetAttributes.PrimaryAttributes)
                Assert.AreEqual(stored[attribute], result[attribute], $"{attribute} was rewritten on an already-conserving switch");
        }

        /// <summary>
        /// CATCHES: a surplus deposited anywhere other than the LOWEST attribute.
        ///
        /// The first case is the repo owner's own reported arrangement (STR/END/COORD 100,
        /// QUICK/FOCUS/SELF 10) plus one innate augmentation. On its own it does NOT discriminate, and
        /// that was measured rather than assumed: a control run that replaced the value comparison with
        /// enum order alone still passed it, because Strength, Endurance and Coordination are already at
        /// the ceiling and get skipped by the walk either way.
        ///
        /// The second case is the discriminating one. Strength (enum 1) sits BELOW the ceiling at 50
        /// while Focus (enum 5) is the lowest at 10, so an enum-order walk deposits on Strength and a
        /// lowest-first walk deposits on Focus. Lowest-first is what verify-attributes fix chooses, and
        /// it is what was approved.
        /// </summary>
        [TestMethod]
        public void Reconcile_Surplus_LandsOnTheLowestAttributeFirst()
        {
            var stored = Arrangement(100, 100, 10, 100, 10, 10);

            var result = FacetAttributes.Reconcile(stored, 335, out var surplus, out var shortfall);

            Assert.IsNotNull(result);
            Assert.AreEqual(5u, surplus);
            Assert.AreEqual(0u, shortfall);

            Assert.AreEqual(15u, result[PropertyAttribute.Quickness], "Quickness is the lowest attribute at the lowest enum value, so it takes the surplus");
            Assert.AreEqual(100u, result[PropertyAttribute.Strength]);
            Assert.AreEqual(10u, result[PropertyAttribute.Focus]);
            Assert.AreEqual(10u, result[PropertyAttribute.Self]);
            Assert.AreEqual(335u, FacetAttributes.Sum(result));

            // The discriminating case - see the remarks.
            var mixed = Arrangement(50, 100, 100, 100, 10, 100);

            var mixedResult = FacetAttributes.Reconcile(mixed, 465, out var mixedSurplus, out _);

            Assert.AreEqual(5u, mixedSurplus);
            Assert.AreEqual(15u, mixedResult[PropertyAttribute.Focus], "Focus is the LOWEST at 10 and must take the surplus");
            Assert.AreEqual(50u, mixedResult[PropertyAttribute.Strength], "Strength is below the ceiling and first in enum order, but it is not the lowest");
            Assert.AreEqual(465u, FacetAttributes.Sum(mixedResult));
        }

        /// <summary>
        /// CATCHES: a tiebreak on DISPLAY order rather than enum order. The character sheet lists
        /// Coordination before Quickness while the enum orders Quickness (3) before Coordination (4), so
        /// this arrangement - tied at 10 on exactly those two - is the only pair that tells them apart.
        /// </summary>
        [TestMethod]
        public void Reconcile_SurplusTie_BreaksOnEnumOrderNotDisplayOrder()
        {
            var stored = Arrangement(100, 100, 10, 10, 100, 100);

            var result = FacetAttributes.Reconcile(stored, 425, out var surplus, out _);

            Assert.AreEqual(5u, surplus);
            Assert.AreEqual(15u, result[PropertyAttribute.Quickness], "Quickness is enum value 3, Coordination is 4 - the tie goes to Quickness");
            Assert.AreEqual(10u, result[PropertyAttribute.Coordination]);
            Assert.AreEqual(425u, FacetAttributes.Sum(result));
        }

        /// <summary>
        /// CATCHES: a walk that stops at the first attribute, or one that spreads the surplus evenly.
        /// The lowest attribute must be filled to the 100 ceiling before the next one takes anything,
        /// and the deposit order must not be recomputed as values change.
        /// </summary>
        [TestMethod]
        public void Reconcile_SurplusSpillsPastACeiling_FillsLowestToCeilingThenMovesOn()
        {
            var stored = Arrangement(10, 10, 10, 10, 10, 10);

            var result = FacetAttributes.Reconcile(stored, 560, out var surplus, out _);

            Assert.AreEqual(500u, surplus);

            Assert.AreEqual(100u, result[PropertyAttribute.Strength]);
            Assert.AreEqual(100u, result[PropertyAttribute.Endurance]);
            Assert.AreEqual(100u, result[PropertyAttribute.Quickness]);
            Assert.AreEqual(100u, result[PropertyAttribute.Coordination]);
            Assert.AreEqual(100u, result[PropertyAttribute.Focus]);
            Assert.AreEqual(60u, result[PropertyAttribute.Self], "the last attribute in the walk takes only what is left, not a full 90");

            Assert.AreEqual(560u, FacetAttributes.Sum(result));
        }

        /// <summary>
        /// CATCHES: a hard clamp at 100 that DESTROYS the leftover points. Values above 100 are already
        /// reachable when attribute_augmentation_safety_cap is false, so clamping here would silently
        /// delete real innate points - exactly the failure this class exists to prevent. Conservation is
        /// the assertion that fails if a clamp is ever added.
        /// </summary>
        [TestMethod]
        public void Reconcile_AllSixAtTheCeiling_PutsTheRemainderOnTheFirstInOrder_AndStillConserves()
        {
            var stored = Arrangement(100, 100, 100, 100, 100, 100);

            var result = FacetAttributes.Reconcile(stored, 605, out var surplus, out var shortfall);

            Assert.IsNotNull(result);
            Assert.AreEqual(5u, surplus);
            Assert.AreEqual(0u, shortfall);

            Assert.AreEqual(105u, result[PropertyAttribute.Strength], "everything is tied at the ceiling, so the first attribute in enum order takes the whole remainder");
            Assert.AreEqual(605u, FacetAttributes.Sum(result), "the remainder must not be clamped away");
        }

        /// <summary>
        /// CATCHES: a walk that fills every ceiling and then drops what is left. Same guarantee as the
        /// test above, but reached through the walk rather than around it.
        /// </summary>
        [TestMethod]
        public void Reconcile_SurplusExceedsEveryCeiling_KeepsTheRemainder()
        {
            var stored = Arrangement(10, 10, 10, 10, 10, 10);

            var result = FacetAttributes.Reconcile(stored, 660, out var surplus, out _);

            Assert.AreEqual(600u, surplus);
            Assert.AreEqual(160u, result[PropertyAttribute.Strength]);
            Assert.AreEqual(660u, FacetAttributes.Sum(result));
        }

        /// <summary>
        /// CATCHES: a deficit silently "fixed" by scaling the arrangement down. Only an admin
        /// subtraction can produce this, and every arrangement summing to the smaller live total is one
        /// the player did not choose - so the answer is null plus a named shortfall, and the caller
        /// refuses BEFORE mutating anything.
        /// </summary>
        [TestMethod]
        public void Reconcile_Deficit_ReturnsNullAndNamesTheShortfall()
        {
            var stored = Arrangement(100, 100, 10, 100, 10, 10);

            var result = FacetAttributes.Reconcile(stored, 320, out var surplus, out var shortfall);

            Assert.IsNull(result);
            Assert.AreEqual(10u, shortfall);
            Assert.AreEqual(0u, surplus);
        }

        /// <summary>
        /// CATCHES: a partial arrangement being applied. It would leave the unnamed attributes at their
        /// live values while the surplus had already been balanced against a short stored sum, breaking
        /// conservation in the one place nothing downstream would notice. Null with NO shortfall
        /// distinguishes it from the admin-subtraction case above.
        /// </summary>
        [TestMethod]
        public void Reconcile_PartialArrangement_ReturnsNullWithNoShortfall()
        {
            var partial = Arrangement(100, 100, 10, 100, 10, 10);
            partial.Remove(PropertyAttribute.Self);

            var result = FacetAttributes.Reconcile(partial, 320, out var surplus, out var shortfall);

            Assert.IsNull(result);
            Assert.AreEqual(0u, shortfall, "a missing attribute is not a shortfall - the caller must not report it as one");
            Assert.AreEqual(0u, surplus);

            Assert.IsNull(FacetAttributes.Reconcile(null, 330, out _, out var nullShortfall));
            Assert.AreEqual(0u, nullShortfall);
        }

        /// <summary>
        /// THE INVARIANT ITSELF, across a table: whatever comes back, its sum is the live sum. Every
        /// other test here pins one route through the reconciliation; this one pins the property all of
        /// them exist to protect, so a future route added without a test of its own is still covered.
        ///
        /// CATCHES: any deposit rule that loses or invents a point - a clamp, an off-by-one on the
        /// ceiling room, a surplus applied twice, or a break that leaves the loop early.
        /// </summary>
        [TestMethod]
        public void Reconcile_AlwaysConservesTheLiveSum()
        {
            var cases = new List<(Dictionary<PropertyAttribute, uint> stored, uint liveSum)>
            {
                (Arrangement(100, 100, 10, 100, 10, 10), 330),
                (Arrangement(100, 100, 10, 100, 10, 10), 335),
                (Arrangement(100, 100, 10, 100, 10, 10), 500),
                (Arrangement(10, 10, 10, 10, 10, 10), 60),
                (Arrangement(10, 10, 10, 10, 10, 10), 61),
                (Arrangement(10, 10, 10, 10, 10, 10), 599),
                (Arrangement(10, 10, 10, 10, 10, 10), 600),
                (Arrangement(10, 10, 10, 10, 10, 10), 601),
                (Arrangement(100, 100, 100, 100, 100, 100), 600),
                (Arrangement(100, 100, 100, 100, 100, 100), 601),
                (Arrangement(104, 100, 100, 100, 100, 100), 610),
                (Arrangement(1, 1, 1, 1, 1, 1), 6),
                (Arrangement(1, 1, 1, 1, 1, 1), 9999),
                (Arrangement(55, 60, 65, 70, 75, 80), 405),
                (Arrangement(55, 60, 65, 70, 75, 80), 406),
                (Arrangement(55, 60, 65, 70, 75, 80), 1000),
            };

            foreach (var (stored, liveSum) in cases)
            {
                var storedSum = FacetAttributes.Sum(stored);

                var result = FacetAttributes.Reconcile(stored, liveSum, out var surplus, out var shortfall);

                Assert.IsNotNull(result, $"live {liveSum} vs stored {storedSum} should reconcile, not refuse");
                Assert.AreEqual(0u, shortfall);
                Assert.AreEqual(liveSum - storedSum, surplus, $"live {liveSum} vs stored {storedSum}: the reported surplus must be the whole difference");
                Assert.AreEqual(liveSum, FacetAttributes.Sum(result), $"live {liveSum} vs stored {storedSum}: sum(result) must equal the live sum");
                Assert.AreEqual(6, result.Count, "the result always names exactly the six primaries");
            }
        }

        /// <summary>
        /// The switch-summary line. A reconciled surplus means the arrangement the player is now
        /// standing on is NOT the one the slot remembered, so it has to be named - silently is how a
        /// correct reconciliation looks identical to a bug.
        ///
        /// CATCHES: a line that reports only the total and not where it landed; a line emitted when
        /// nothing was deposited; and a deposit spanning several attributes reported as a single one.
        /// </summary>
        [TestMethod]
        public void ComposeAttributeSurplusLine_NamesEveryAttributeThatGained()
        {
            var stored = Arrangement(100, 100, 10, 100, 10, 10);

            var single = FacetAttributes.Reconcile(stored, 335, out var singleSurplus, out _);
            var singleLine = Player.ComposeAttributeSurplusLine(stored, single, singleSurplus);

            StringAssert.Contains(singleLine, "Quickness +5");
            Assert.IsFalse(singleLine.Contains("Strength"), "an attribute that gained nothing must not be listed");

            // A surplus large enough to fill Quickness to the ceiling and spill into Focus - the case a
            // single "it went here" out-parameter would report wrongly.
            var spread = FacetAttributes.Reconcile(stored, 500, out var spreadSurplus, out _);
            var spreadLine = Player.ComposeAttributeSurplusLine(stored, spread, spreadSurplus);

            StringAssert.Contains(spreadLine, "Quickness +90");
            StringAssert.Contains(spreadLine, "Focus +80");

            Assert.IsNull(Player.ComposeAttributeSurplusLine(stored, stored, 0), "no surplus, no line");
            Assert.IsNull(Player.ComposeAttributeSurplusLine(null, stored, 5));
            Assert.IsNull(Player.ComposeAttributeSurplusLine(stored, null, 5));
        }

        /// <summary>
        /// CATCHES: a Reconcile that mutates the caller's stored dictionary in place. The switch path
        /// reads `stored` again afterwards to work out where the surplus landed, so an in-place deposit
        /// would make that diff empty and the player would never be told.
        /// </summary>
        [TestMethod]
        public void Reconcile_DoesNotMutateTheStoredArrangement()
        {
            var stored = Arrangement(100, 100, 10, 100, 10, 10);

            FacetAttributes.Reconcile(stored, 400, out _, out _);

            Assert.AreEqual(10u, stored[PropertyAttribute.Quickness]);
            Assert.AreEqual(330u, FacetAttributes.Sum(stored));
        }
    }
}
