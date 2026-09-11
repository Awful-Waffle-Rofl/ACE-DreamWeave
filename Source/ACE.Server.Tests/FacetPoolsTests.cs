using System;
using System.Collections.Generic;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity.Facets;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The three spendable pools are carried as a DELTA across a facet switch rather than stored per
    /// slot or re-derived from a lifetime total, so that XP, credits and points earned while standing on
    /// one facet are immediately available on all of them.
    ///
    /// These tests pin the arithmetic, and in particular that a round trip CONSERVES each pool exactly.
    /// Conservation is the property this whole design exists to protect, and it has now failed in review
    /// THREE times, in two distinct shapes:
    ///
    ///   1. A pool re-derived from a lifetime total rather than carried as a delta (class ability points,
    ///      refunded by every switch; skill credits, zeroed by every switch). Pinned by
    ///      AvailableClassAbilityPointsAfterSwap_DoesNotRefundPointsSpentOutsideTheBuild.
    ///
    ///   2. A cost delegate that prices a skill generically instead of reproducing what the engine
    ///      charged. The delta form does NOT rescue this on its own: a pricing error cancels across a swap
    ///      only when the skill's SAC is the same on both sides, and SAC is per-build. The
    ///      AvailableSkillCreditsAfterSwap_AugSpecSkillTrainedInTheStoredRow_* pair shows the mechanism
    ///      and the magnitude; Player.ComposeSkillCreditCost_* at the bottom of the file is what actually
    ///      pins the production decision.
    ///
    /// This file has twice asserted the implementation back to itself rather than testing it, in two
    /// different ways, and both are worth remembering before adding a case here. First by CONFIGURATION:
    /// the original aug tests put the aug skill Specialized on both sides, the one arrangement in which
    /// any pricing error cancels. Then by SUPPLYING THE ANSWER: their replacements hand FacetPools a
    /// locally-written aug-aware delegate, so they pass identically against the pre-fix commit, where
    /// production still priced generically. A test whose delegate encodes the behaviour under test is not
    /// testing that behaviour.
    ///
    /// The credit costs come in through a delegate because ACE.Server.Tests has no client dat files, and
    /// that delegate is load-bearing rather than a mere test seam: in production it is the INSTANCE method
    /// Player.LookupSkillCreditCost. The final block in this file tests its decision directly through
    /// Player.ComposeSkillCreditCost, and states exactly what remains uncovered.
    /// </summary>
    [TestClass]
    public class FacetPoolsTests
    {
        /// <summary>Fixed stand-in costs. Real values come from portal.dat's SkillTable at the call site.</summary>
        private static SkillCreditCost Costs(Skill skill) => skill switch
        {
            Skill.WarMagic => new SkillCreditCost(8, 8),
            Skill.Bow      => new SkillCreditCost(6, 10),
            _              => new SkillCreditCost(4, 4),
        };

        [TestMethod]
        public void SkillCreditsSpent_ChargesTrainedOnce_AndSpecializedOnTop()
        {
            var skills = new List<FacetSkillEntry>
            {
                new FacetSkillEntry { Skill = Skill.WarMagic, Sac = SkillAdvancementClass.Specialized },
                new FacetSkillEntry { Skill = Skill.Bow,      Sac = SkillAdvancementClass.Trained },
            };

            // WarMagic: 8 trained + 8 upgrade = 16. Bow: 6 trained. Total 22.
            Assert.AreEqual(22u, FacetPools.SkillCreditsSpent(skills, Costs));
        }

        [TestMethod]
        public void SkillCreditsSpent_IgnoresUntrained()
        {
            var skills = new List<FacetSkillEntry>
            {
                new FacetSkillEntry { Skill = Skill.WarMagic, Sac = SkillAdvancementClass.Untrained },
                new FacetSkillEntry { Skill = Skill.Bow,      Sac = SkillAdvancementClass.Trained },
            };

            Assert.AreEqual(6u, FacetPools.SkillCreditsSpent(skills, Costs));
        }

        [TestMethod]
        public void SkillCreditsSpent_EmptyBuildSpendsNothing()
        {
            Assert.AreEqual(0u, FacetPools.SkillCreditsSpent(new List<FacetSkillEntry>(), Costs));
        }

        [TestMethod]
        public void AvailableSkillCreditsAfterSwap_ReleasesTheOutgoingSpend_AndCommitsTheIncoming()
        {
            // Standing on a build costing 22 credits with 30 unspent; swapping to a build costing 40 must
            // leave 12.
            Assert.AreEqual(12, FacetPools.AvailableSkillCreditsAfterSwap(30, 22u, 40u, out var shortfall));
            Assert.AreEqual(0L, shortfall);
        }

        [TestMethod]
        public void AvailableSkillCreditsAfterSwap_ClampsAtZero_AndReportsTheExactShortfall()
        {
            // A negative pool would be a data-integrity failure, not a playable state. Clamp so the client
            // is never handed a negative count, and report the deficit so the caller refuses the switch.
            Assert.AreEqual(0, FacetPools.AvailableSkillCreditsAfterSwap(10, 0u, 22u, out var shortfall));
            Assert.AreEqual(12L, shortfall);
        }

        /// <summary>
        /// What the production delegate (the INSTANCE Player.LookupSkillCreditCost) returns for a
        /// character who holds AugmentationSpecializeSalvaging: the upgrade is free, because the engine
        /// specializes an aug-spec skill for zero.
        /// </summary>
        private static SkillCreditCost AugAwareCosts(Skill skill) => skill switch
        {
            Skill.Salvaging => new SkillCreditCost(4, 0),
            _               => new SkillCreditCost(4, 4),
        };

        /// <summary>
        /// What a GENERIC dat lookup returns for the same character: the dat's
        /// UpgradeCostFromTrainedToSpecialized of 999, which the engine never charged. Kept as a named
        /// delegate so the test below can pin that this is where the 999 comes from.
        /// </summary>
        private static SkillCreditCost GenericDatCosts(Skill skill) => skill switch
        {
            Skill.Salvaging => new SkillCreditCost(4, 999),
            _               => new SkillCreditCost(4, 4),
        };

        /// <summary>
        /// The discriminating pair of builds, and the whole point of these two tests. Salvaging is
        /// AlwaysTrained AND in AugSpecSkills, so it is present in every build - but its SAC is per-build.
        /// A slot row written BEFORE the player bought AugmentationSpecializeSalvaging stores it TRAINED
        /// while the live character has it SPECIALIZED, which is reachable in ordinary play by simply
        /// visiting the second slot once before buying the augmentation.
        /// </summary>
        private static (List<FacetSkillEntry> outgoing, List<FacetSkillEntry> incoming) AugSkillSpecializedLiveTrainedInRow()
        {
            var outgoing = new List<FacetSkillEntry>
            {
                new FacetSkillEntry { Skill = Skill.Salvaging, Sac = SkillAdvancementClass.Specialized },
                new FacetSkillEntry { Skill = Skill.WarMagic,  Sac = SkillAdvancementClass.Trained },
            };

            var incoming = new List<FacetSkillEntry>
            {
                new FacetSkillEntry { Skill = Skill.Salvaging, Sac = SkillAdvancementClass.Trained },
                new FacetSkillEntry { Skill = Skill.WarMagic,  Sac = SkillAdvancementClass.Trained },
            };

            return (outgoing, incoming);
        }

        [TestMethod]
        public void AvailableSkillCreditsAfterSwap_AugSpecSkillTrainedInTheStoredRow_ConservesUnderTheAugAwareLookup()
        {
            // CRITICAL regression. The engine specializes an augmentation-specialized skill for ZERO
            // credits - Player_Skills.TrainSkill auto-calls SpecializeSkill(skill, 0, false) when the aug
            // is held, and AugmentationDevice.DoAugmentation sets SAC directly while charging experience
            // only - so an aug-aware lookup must price the upgrade at zero.
            //
            // The delta form alone does NOT save a generic lookup here, which is the thing an earlier
            // version of this test got wrong: SkillCreditsSpent charges Trained+Specialized for a
            // Specialized entry and Trained alone for a Trained one, so the error cancels only when the
            // SAC matches on both sides. Here it does not, which is ordinary rather than exotic.
            var (outgoing, incoming) = AugSkillSpecializedLiveTrainedInRow();

            var outgoingSpent = FacetPools.SkillCreditsSpent(outgoing, AugAwareCosts);
            var incomingSpent = FacetPools.SkillCreditsSpent(incoming, AugAwareCosts);

            // Salvaging's UPGRADE costs this character nothing either way, so both sides price identically
            // at Salvaging's 4 trained plus WarMagic's 4.
            Assert.AreEqual(8u, outgoingSpent);
            Assert.AreEqual(8u, incomingSpent);

            var after = FacetPools.AvailableSkillCreditsAfterSwap(92, outgoingSpent, incomingSpent, out var shortfall);

            Assert.AreEqual(92, after, "the pool must be untouched: the engine charged nothing for either state of Salvaging");
            Assert.AreEqual(0L, shortfall);
        }

        [TestMethod]
        public void AvailableSkillCreditsAfterSwap_AugSpecSkillTrainedInTheStoredRow_GenericLookupWouldManufactureCredits()
        {
            // The negative half of the pair. What it pins is the MAGNITUDE and mechanism of the defect
            // under a generic delegate: that a single SAC difference on one aug-spec skill releases 999
            // credits, which is catastrophic against a lifetime budget in the low hundreds and is
            // repeatable once per aug-spec skill by deferring each augmentation purchase until after the
            // target slot's row exists.
            //
            // It does NOT pin that production uses an aug-aware delegate - it never reaches
            // Player.LookupSkillCreditCost, and an earlier version of this comment wrongly claimed it did.
            // ComposeSkillCreditCost_* below is the test that actually fails when the pricing decision is
            // reverted; see its remarks for what remains uncovered even then.
            var (outgoing, incoming) = AugSkillSpecializedLiveTrainedInRow();

            var outgoingSpent = FacetPools.SkillCreditsSpent(outgoing, GenericDatCosts);
            var incomingSpent = FacetPools.SkillCreditsSpent(incoming, GenericDatCosts);

            Assert.AreEqual(1007u, outgoingSpent, "generic pricing charges the dat's 999 upgrade on the Specialized side");
            Assert.AreEqual(8u, incomingSpent, "and nothing for it on the Trained side");

            var after = FacetPools.AvailableSkillCreditsAfterSwap(92, outgoingSpent, incomingSpent, out var shortfall);

            Assert.AreEqual(1091, after, "this is the bug: 999 credits released against an engine charge of zero");
            Assert.AreEqual(0L, shortfall);
            Assert.IsTrue(after > 114, "the manufactured pool exceeds the maximum lifetime skill credits any character can hold");
        }

        [TestMethod]
        public void AvailableSkillCreditsAfterSwap_ConservesCreditsAcrossARoundTrip()
        {
            // Build A specializes an ordinary (non-aug) skill; build B specializes a different one. Both
            // carry the aug-spec Salvaging at its true zero cost, and its SAC deliberately DIFFERS between
            // the two builds, so this round trip would not close if the delegate were generic.
            var buildA = new List<FacetSkillEntry>
            {
                new FacetSkillEntry { Skill = Skill.Salvaging, Sac = SkillAdvancementClass.Specialized },
                new FacetSkillEntry { Skill = Skill.WarMagic,  Sac = SkillAdvancementClass.Specialized },
            };

            var buildB = new List<FacetSkillEntry>
            {
                new FacetSkillEntry { Skill = Skill.Salvaging, Sac = SkillAdvancementClass.Trained },
                new FacetSkillEntry { Skill = Skill.Bow,       Sac = SkillAdvancementClass.Specialized },
                new FacetSkillEntry { Skill = Skill.WarMagic,  Sac = SkillAdvancementClass.Trained },
            };

            var costA = FacetPools.SkillCreditsSpent(buildA, AugAwareCosts);
            var costB = FacetPools.SkillCreditsSpent(buildB, AugAwareCosts);

            const int start = 92;

            var away = FacetPools.AvailableSkillCreditsAfterSwap(start, costA, costB, out var awayShortfall);
            var back = FacetPools.AvailableSkillCreditsAfterSwap(away, costB, costA, out var backShortfall);

            Assert.AreEqual(start, back, "out and back must land on the starting pool exactly");
            Assert.AreEqual(0L, awayShortfall);
            Assert.AreEqual(0L, backShortfall);
        }

        [TestMethod]
        public void AvailableSkillCreditsAfterSwap_ResetSkillDroppedTheAugSkillToTrained_DoesNotStrandThePlayer()
        {
            // The same asymmetry in the other direction, and the reason a shortfall refusal is not a
            // sufficient answer to it on its own. Player_Skills.ResetSkill takes a tinkering skill
            // Specialized -> Trained WITHOUT refunding the upgrade cost (it is reachable in play via
            // EmoteManager), so the live build can be Trained while the stored row is Specialized.
            //
            // Under a generic lookup the switch back would be short 999 forever, with nothing the player
            // could untrain to recover it. Under the aug-aware lookup both sides price at zero.
            var outgoing = new List<FacetSkillEntry>
            {
                new FacetSkillEntry { Skill = Skill.Salvaging, Sac = SkillAdvancementClass.Trained },
            };

            var incoming = new List<FacetSkillEntry>
            {
                new FacetSkillEntry { Skill = Skill.Salvaging, Sac = SkillAdvancementClass.Specialized },
            };

            var outgoingSpent = FacetPools.SkillCreditsSpent(outgoing, AugAwareCosts);
            var incomingSpent = FacetPools.SkillCreditsSpent(incoming, AugAwareCosts);

            var after = FacetPools.AvailableSkillCreditsAfterSwap(3, outgoingSpent, incomingSpent, out var shortfall);

            Assert.AreEqual(3, after);
            Assert.AreEqual(0L, shortfall, "a character with 3 credits must not be permanently locked out of their own stored build");

            // And the control: the generic lookup is what would have stranded them.
            var genericIncoming = FacetPools.SkillCreditsSpent(incoming, GenericDatCosts);
            FacetPools.AvailableSkillCreditsAfterSwap(3, FacetPools.SkillCreditsSpent(outgoing, GenericDatCosts), genericIncoming, out var genericShortfall);

            // 3 + 4 - 1003: the pool clamps to zero and the player is told they are short 996 credits, on
            // a switch back to a build they already owned.
            Assert.AreEqual(996L, genericShortfall);
        }

        [TestMethod]
        public void AvailableClassAbilityPointsAfterSwap_ReleasesTheOutgoingSpend_AndCommitsTheIncoming()
        {
            Assert.AreEqual(14, FacetPools.AvailableClassAbilityPointsAfterSwap(20, 6, 12, out var shortfall));
            Assert.AreEqual(0, shortfall);
        }

        [TestMethod]
        public void AvailableClassAbilityPointsAfterSwap_ClampsAtZero_AndReportsTheExactShortfall()
        {
            Assert.AreEqual(0, FacetPools.AvailableClassAbilityPointsAfterSwap(5, 0, 9, out var shortfall));
            Assert.AreEqual(4, shortfall);
        }

        [TestMethod]
        public void AvailableClassAbilityPointsAfterSwap_DoesNotRefundPointsSpentOutsideTheBuild()
        {
            // CRITICAL regression, and the reason this pool is not derived from
            // TotalClassAbilityPointsEarned. Class ability points are also a vendor alternate currency
            // (Player_Bank.DebitBankedAlternateCurrency, wcid 1001010) and back prepaid training vouchers
            // (Player_ClassAbilityTokens), both of which debit AvailableClassAbilityPoints while leaving
            // TotalClassAbilityPointsEarned alone.
            //
            // The scenario the OLD ledger form made free: 500 points earned, nothing learned, so 500
            // available; buy a 100-point voucher, leaving 400 with the voucher in the pack; then switch.
            // Both builds have zero abilities, so a recompute of (500 - 0) restored the 100 AND kept the
            // voucher - repeatable without limit, since switching is uncooldowned.
            const int totalEarned = 500;
            const int afterVoucherPurchase = 400;
            const int outgoingBuildSpend = 0;
            const int incomingBuildSpend = 0;

            var after = FacetPools.AvailableClassAbilityPointsAfterSwap(
                afterVoucherPurchase, outgoingBuildSpend, incomingBuildSpend, out var shortfall);

            Assert.AreEqual(afterVoucherPurchase, after, "a swap between two zero-ability builds must not change the pool");
            Assert.AreEqual(0, shortfall);
            Assert.AreNotEqual(totalEarned, after, "the ledger form would have restored the vendor spend here");
        }

        [TestMethod]
        public void AvailableClassAbilityPointsAfterSwap_ConservesPointsAcrossARoundTrip()
        {
            // Out to a cheaper build and back must land on the starting pool exactly, with a vendor spend
            // already baked into it and staying spent throughout.
            const int start = 120;   // 500 earned, 100 spent at a vendor, 280 committed to the current build
            const int buildA = 280;
            const int buildB = 90;

            var away = FacetPools.AvailableClassAbilityPointsAfterSwap(start, buildA, buildB, out var awayShortfall);
            var back = FacetPools.AvailableClassAbilityPointsAfterSwap(away, buildB, buildA, out var backShortfall);

            Assert.AreEqual(310, away);
            Assert.AreEqual(start, back);
            Assert.AreEqual(0, awayShortfall);
            Assert.AreEqual(0, backShortfall);
        }

        [TestMethod]
        public void TotalPp_SumsWithoutOverflowingUint()
        {
            // Two skills near uint.MaxValue must not wrap. This is why the accumulator is ulong.
            var skills = new List<FacetSkillEntry>
            {
                new FacetSkillEntry { Skill = Skill.WarMagic, Pp = 4_000_000_000u },
                new FacetSkillEntry { Skill = Skill.Bow,      Pp = 4_000_000_000u },
            };

            Assert.AreEqual(8_000_000_000UL, FacetPools.TotalPp(skills));
        }

        [TestMethod]
        public void AvailableExperienceAfterSwap_ReleasesTheOutgoingSpend_AndCommitsTheIncoming()
        {
            // Standing on a build with 500 invested, holding 100 unspent; swapping to a build with
            // 300 invested must leave 300 unspent.
            Assert.AreEqual(300L, FacetPools.AvailableExperienceAfterSwap(100L, 500UL, 300UL, out var shortfall));
            Assert.AreEqual(0UL, shortfall);
        }

        [TestMethod]
        public void AvailableExperienceAfterSwap_AffordableSwap_ReportsZeroShortfall()
        {
            FacetPools.AvailableExperienceAfterSwap(100L, 500UL, 300UL, out var shortfall);

            Assert.AreEqual(0UL, shortfall);
        }

        [TestMethod]
        public void AvailableExperienceAfterSwap_ConservesExperienceAcrossARoundTrip()
        {
            // The core invariant. Out to another build and back must land on the starting pool exactly,
            // whatever the two builds had invested. This only holds when both legs are affordable.
            const long start = 12_345_678L;
            const ulong buildA = 900_000_000UL;
            const ulong buildB = 250_000_000UL;

            var away = FacetPools.AvailableExperienceAfterSwap(start, buildA, buildB, out var awayShortfall);
            var back = FacetPools.AvailableExperienceAfterSwap(away, buildB, buildA, out var backShortfall);

            Assert.AreEqual(start, back);
            Assert.AreEqual(0UL, awayShortfall);
            Assert.AreEqual(0UL, backShortfall);
        }

        [TestMethod]
        public void AvailableExperienceAfterSwap_CarriesForwardExperienceEarnedWhileAway()
        {
            // Earning XP on facet 2 must survive the trip back to facet 1. This is the case that
            // storing the pool per slot would silently delete.
            const long start = 0L;
            const ulong buildA = 500UL;
            const ulong buildB = 200UL;

            var onB = FacetPools.AvailableExperienceAfterSwap(start, buildA, buildB, out _);   // 300
            var earned = onB + 1_000L;                                                           // 1300
            var backOnA = FacetPools.AvailableExperienceAfterSwap(earned, buildB, buildA, out var backOnAShortfall);

            Assert.AreEqual(1_000L, backOnA);
            Assert.AreEqual(0UL, backOnAShortfall);
        }

        [TestMethod]
        public void AvailableExperienceAfterSwap_ClampsAtZero_RatherThanGoingNegative()
        {
            Assert.AreEqual(0L, FacetPools.AvailableExperienceAfterSwap(0L, 0UL, 5_000UL, out _));
        }

        [TestMethod]
        public void AvailableExperienceAfterSwap_UnaffordableSwap_ReportsExactShortfall()
        {
            // incomingPp exceeds current + outgoingPp: the pool clamps to zero, but the caller must be
            // told exactly how much was not covered so it can refuse the switch.
            var result = FacetPools.AvailableExperienceAfterSwap(10L, 20UL, 100UL, out var shortfall);

            Assert.AreEqual(0L, result);
            Assert.AreEqual(70UL, shortfall);
        }

        [TestMethod]
        public void AvailableExperienceAfterSwap_UnaffordableSwap_DoesNotRecoverOnSwapBack_RegressionForXpDuplication()
        {
            // The exploit this guards against: a caller that proceeds with an unaffordable switch anyway
            // (instead of refusing it) silently loses the shortfall. Swapping back does NOT restore the
            // original pool - the caller MUST refuse the switch on a non-zero shortfall, before any
            // mutation, rather than ever letting this state be reached.
            const long start = 100L;
            const ulong outgoing = 50UL;
            const ulong incoming = 500UL;

            var away = FacetPools.AvailableExperienceAfterSwap(start, outgoing, incoming, out var shortfall);

            Assert.AreEqual(0L, away);
            Assert.AreEqual(350UL, shortfall);

            var back = FacetPools.AvailableExperienceAfterSwap(away, incoming, outgoing, out var backShortfall);

            Assert.AreNotEqual(start, back);
            Assert.AreEqual(0UL, backShortfall);
        }

        // ==================================================================================
        // The production cost delegate's DECISION.
        //
        // Everything above this line feeds FacetPools a hand-written delegate, which means it can only
        // ever assert FacetPools' own arithmetic - not that production computes the price correctly.
        // That gap is how the augmentation fix first shipped with no coverage at all: the whole fix was
        // one ternary inside Player.LookupSkillCreditCost, which no test reached, so the suite stayed
        // green with the defect restored.
        //
        // Player.ComposeSkillCreditCost exists to close it: the pricing decision, split out from the dat
        // read (DatManager.PortalDat is unreachable in this project) and exposed via InternalsVisibleTo.
        //
        // What these tests DO catch: any change to the decision itself - dropping the flag, zeroing the
        // trained cost as well, inverting the condition.
        // What they still do NOT catch: the three lines of wiring left in LookupSkillCreditCost, which
        // need a live DatManager and a live Player. A change that kept the shape but passed a constant
        // false would pass this suite. That residual is queued as a live check rather than papered over,
        // and the reflection test below pins the structural half of it.
        // ==================================================================================

        // Salvaging's real portal.dat figures, as probed this session by the final reviewer: the trained
        // cost is 0 (it is AlwaysTrained, granted at creation) and the generic upgrade cost is 999. They
        // are used here for documentary fidelity only - the assertions below are about the COMPOSITION and
        // hold for any pair of inputs, which is deliberate so that a misremembered dat figure cannot make
        // the test wrong.
        private const int SalvagingTrainedCost = 0;
        private const int SalvagingUpgradeCost = 999;

        [TestMethod]
        public void ComposeSkillCreditCost_AugmentationHeld_PricesTheUpgradeAtZero_AndLeavesTrainedAlone()
        {
            var cost = Player.ComposeSkillCreditCost(SalvagingTrainedCost, SalvagingUpgradeCost, specializedFreeByAugmentation: true);

            Assert.AreEqual(SalvagingTrainedCost, cost.Trained, "an augmentation grants the specialization, never the training");
            Assert.AreEqual(0, cost.Specialized, "the engine specializes an aug-spec skill for zero, so the dat's 999 must not be charged");
        }

        [TestMethod]
        public void ComposeSkillCreditCost_AugmentationNotHeld_ChargesTheDatUpgradeCost()
        {
            var cost = Player.ComposeSkillCreditCost(SalvagingTrainedCost, SalvagingUpgradeCost, specializedFreeByAugmentation: false);

            Assert.AreEqual(SalvagingTrainedCost, cost.Trained);
            Assert.AreEqual(SalvagingUpgradeCost, cost.Specialized, "without the augmentation the player really does pay the dat cost");
        }

        [TestMethod]
        public void ComposeSkillCreditCost_OnlyTheUpgradeIsEverZeroed()
        {
            // A second, deliberately different pair, so the assertions above cannot be satisfied by an
            // implementation that happens to return constants matching Salvaging's figures.
            var free = Player.ComposeSkillCreditCost(6, 10, specializedFreeByAugmentation: true);
            var paid = Player.ComposeSkillCreditCost(6, 10, specializedFreeByAugmentation: false);

            Assert.AreEqual(6, free.Trained);
            Assert.AreEqual(0, free.Specialized);

            Assert.AreEqual(6, paid.Trained);
            Assert.AreEqual(10, paid.Specialized);

            Assert.AreNotEqual(paid.Specialized, free.Specialized, "the flag must actually change the price, or the fix is inert");
        }

        [TestMethod]
        public void LookupSkillCreditCost_IsAnInstanceMethod_SoThePriceCanDependOnTheCharacter()
        {
            // The structural half of "do not revert this to a static generic dat read". A static method
            // group still binds from an instance call site, so reverting it would compile silently and the
            // rest of the suite would stay green; this is the assertion that would not.
            //
            // It is a real invariant rather than a shape check: an augmentation is per-character state, so
            // a price that cannot see `this` cannot be the price the engine charged.
            var method = typeof(Player).GetMethod(
                nameof(Player.LookupSkillCreditCost),
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

            Assert.IsNotNull(method, "Player.LookupSkillCreditCost must exist - FacetPools.SkillCreditsSpent takes it as the cost delegate");
            Assert.IsFalse(method.IsStatic, "Player.LookupSkillCreditCost must stay an INSTANCE method: an aug-specialized skill is priced from per-character state");
        }
    }
}
