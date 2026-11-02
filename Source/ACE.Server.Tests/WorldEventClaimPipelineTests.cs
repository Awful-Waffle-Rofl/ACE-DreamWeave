using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The four-stage claim pipeline (slice R3): ResolveEligibility -> ResolvePayout -> ResolveExtras, plus the log
    /// suffix and the denial token. Content-free and pure: no PropertyManager, no Player, no landblock.
    ///
    /// The truth tables are HARD-CODED from the pre-pipeline handler (WorldEventCacheHandler on
    /// train/2026-11-01-1): crate = PaysSuccessCrate(outcome) ? success : consolation; under the presence switch
    /// ResolvePresencePayout decides coal (known 0 with a coal wcid), otherwise PaysCoal(creditEnabled, coal,
    /// hasCredit); the stamp is ResolveStamp(presence, payCoal, paysSuccess). Each row's expected values were
    /// worked from that code by hand, never by calling it here.
    /// </summary>
    [TestClass]
    public class WorldEventClaimPipelineTests
    {
        private const uint SuccessCrate = 100;
        private const uint ConsolationCrate = 200;
        private const uint Coal = 300;

        private static RewardDef Reward(uint coal = Coal, List<uint> bonus = null, bool finalOnly = false) => new RewardDef
        {
            Id = "test",
            SuccessCrateWcid = SuccessCrate,
            ConsolationCrateWcid = ConsolationCrate,
            CoalWcid = coal,
            BonusCrateWcids = bonus ?? new List<uint>(),
            BonusFinalStageOnly = finalOnly
        };

        private static ClaimInputs Inputs(WorldEventOutcome outcome, bool presenceOn, bool known, double fraction, bool creditOn, bool hasCredit, uint coal,
            WorldEventClaimPolicy policy = null, bool secondsKnown = false, double seconds = 0, bool shareKnown = false, double share = 0) => new ClaimInputs
            {
                PresenceEnabled = presenceOn,
                PresenceKnown = known,
                PresenceFraction = fraction,
                PresentSecondsKnown = secondsKnown,
                PresentSeconds = seconds,
                CreditEnabled = creditOn,
                HasCredit = hasCredit,
                DamageShareKnown = shareKnown,
                DamageShare = share,
                PaysSuccess = WorldEvent.PaysSuccessCrate(outcome),
                CoalWcid = coal,
                Policy = policy
            };

        private static ClaimPayout Run(WorldEventOutcome outcome, ClaimInputs inputs, uint coal, WorldEventClaimPolicy policy = null)
        {
            var eligibility = WorldEventClaimRules.ResolveEligibility(inputs);
            return WorldEventClaimRules.ResolvePayout(eligibility, Reward(coal), outcome, policy);
        }

        // ---- Default policy reproduces the pre-pipeline decision -----------------------------------------------

        // Columns: outcome (KillCount success = Success, KillBoss no-show = SuccessBossAbsent, DestroySource-style
        // failures = FailedTimeout / FailedWipe / FailedNoParticipants), presence switch, fraction known, fraction,
        // credit switch, has credit, coal wcid  =>  coal?, wcid handed over, stamp?, stamped multiplier.
        [TestMethod]
        // Credit rule (presence switch off).
        [DataRow(WorldEventOutcome.Success, false, false, 0.0, true, false, Coal, true, Coal, false, 0.0, DisplayName = "credit: success, no credit -> coal")]
        [DataRow(WorldEventOutcome.Success, false, false, 0.0, true, true, Coal, false, SuccessCrate, false, 0.0, DisplayName = "credit: success, credit -> success crate")]
        [DataRow(WorldEventOutcome.Success, false, false, 0.0, false, false, Coal, false, SuccessCrate, false, 0.0, DisplayName = "credit off: success, no credit -> success crate")]
        [DataRow(WorldEventOutcome.Success, false, false, 0.0, true, false, 0u, false, SuccessCrate, false, 0.0, DisplayName = "credit: no coal wcid -> success crate")]
        [DataRow(WorldEventOutcome.Success, false, true, 0.0, true, true, Coal, false, SuccessCrate, false, 0.0, DisplayName = "credit: a known 0 presence is ignored with the switch off")]
        [DataRow(WorldEventOutcome.SuccessBossAbsent, false, false, 0.0, true, true, Coal, false, SuccessCrate, false, 0.0, DisplayName = "credit: boss absent pays success crate")]
        [DataRow(WorldEventOutcome.SuccessBossAbsent, false, false, 0.0, true, false, Coal, true, Coal, false, 0.0, DisplayName = "credit: boss absent, no credit -> coal")]
        [DataRow(WorldEventOutcome.FailedTimeout, false, false, 0.0, true, false, Coal, true, Coal, false, 0.0, DisplayName = "credit: timeout, no credit -> coal")]
        [DataRow(WorldEventOutcome.FailedTimeout, false, false, 0.0, true, true, Coal, false, ConsolationCrate, false, 0.0, DisplayName = "credit: timeout, credit -> consolation")]
        [DataRow(WorldEventOutcome.FailedWipe, false, false, 0.0, true, true, Coal, false, ConsolationCrate, false, 0.0, DisplayName = "credit: wipe, credit -> consolation")]
        [DataRow(WorldEventOutcome.FailedWipe, false, false, 0.0, false, false, Coal, false, ConsolationCrate, false, 0.0, DisplayName = "credit off: wipe -> consolation")]
        [DataRow(WorldEventOutcome.FailedNoParticipants, false, false, 0.0, true, false, 0u, false, ConsolationCrate, false, 0.0, DisplayName = "credit: no participants, no coal wcid -> consolation")]
        [DataRow(WorldEventOutcome.FailedNoParticipants, false, false, 0.0, true, false, Coal, true, Coal, false, 0.0, DisplayName = "credit: no participants, no credit -> coal")]
        // Presence rule (switch on): credit inputs are ignored.
        [DataRow(WorldEventOutcome.Success, true, true, 0.0, true, true, Coal, true, Coal, false, 0.0, DisplayName = "presence: known 0 -> coal even with credit")]
        [DataRow(WorldEventOutcome.Success, true, true, 0.0, true, false, 0u, false, SuccessCrate, true, 1.0, DisplayName = "presence: known 0, no coal wcid -> crate at 1")]
        [DataRow(WorldEventOutcome.Success, true, true, 0.4, true, false, Coal, false, SuccessCrate, true, 0.8, DisplayName = "presence: 40% -> crate at 0.8, credit ignored")]
        [DataRow(WorldEventOutcome.Success, true, true, 1.0, false, false, Coal, false, SuccessCrate, true, 2.0, DisplayName = "presence: 100% -> crate at 2")]
        [DataRow(WorldEventOutcome.Success, true, true, 1.7, false, false, Coal, false, SuccessCrate, true, 2.0, DisplayName = "presence: clamped to 2")]
        [DataRow(WorldEventOutcome.Success, true, false, 0.0, true, false, Coal, false, SuccessCrate, true, 1.0, DisplayName = "presence: unknown -> crate at 1, never coal")]
        [DataRow(WorldEventOutcome.Success, true, true, double.NaN, true, false, Coal, false, SuccessCrate, true, 1.0, DisplayName = "presence: NaN -> unknown")]
        [DataRow(WorldEventOutcome.SuccessBossAbsent, true, true, 0.25, true, true, Coal, false, SuccessCrate, true, 0.5, DisplayName = "presence: boss absent 25% -> crate at 0.5")]
        [DataRow(WorldEventOutcome.FailedTimeout, true, true, 0.4, true, true, Coal, false, ConsolationCrate, false, 0.0, DisplayName = "presence: timeout 40% -> unscaled consolation")]
        [DataRow(WorldEventOutcome.FailedWipe, true, true, 0.0, true, true, Coal, true, Coal, false, 0.0, DisplayName = "presence: wipe known 0 -> coal")]
        [DataRow(WorldEventOutcome.FailedWipe, true, true, 0.0, true, true, 0u, false, ConsolationCrate, false, 0.0, DisplayName = "presence: wipe known 0, no coal wcid -> consolation")]
        [DataRow(WorldEventOutcome.FailedNoParticipants, true, false, 0.0, true, false, Coal, false, ConsolationCrate, false, 0.0, DisplayName = "presence: no participants unknown -> consolation")]
        public void DefaultPolicy_MatchesThePrePipelineDecision(WorldEventOutcome outcome, bool presenceOn, bool known, double fraction, bool creditOn, bool hasCredit, uint coal,
            bool expectCoal, uint expectWcid, bool expectStamp, double expectMultiplier)
        {
            foreach (var policy in new[] { null, WorldEventClaimPolicy.Default })
            {
                var payout = Run(outcome, Inputs(outcome, presenceOn, known, fraction, creditOn, hasCredit, coal, policy), coal, policy);

                Assert.AreEqual(ClaimDenial.None, payout.Denial);
                Assert.AreEqual(expectCoal, payout.PayCoal, "coal");
                Assert.AreEqual(expectWcid, payout.PayoutWcid, "wcid handed over");
                Assert.AreEqual(WorldEvent.PaysSuccessCrate(outcome) ? SuccessCrate : ConsolationCrate, payout.CrateWcid, "outcome crate");
                Assert.AreEqual(!presenceOn, payout.Presence.UsesCreditRule, "credit rule iff the switch is off");
                Assert.IsNull(payout.Tier, "a default claim carries no tier");

                var stamp = WorldEventClaimRules.ResolveStamp(payout.Presence, payout.PayCoal, payout.PaysSuccess);
                Assert.AreEqual(expectStamp, stamp.Stamp, "stamp");
                Assert.AreEqual(expectMultiplier, stamp.Multiplier, 1e-9, "multiplier");

                Assert.AreEqual(0, WorldEventClaimRules.ResolveExtras(payout, Reward(coal), true).Count, "no extras on a profile without bonus crates");
                Assert.AreEqual("", WorldEventClaimRules.PipelineLogSuffix(payout, payout.PayCoal, 0, 0), "the claim line is unchanged");
            }
        }

        [TestMethod]
        public void DefaultPolicy_CoalReason_IsTheRuleThatChoseIt()
        {
            var credit = WorldEventClaimRules.ResolveEligibility(Inputs(WorldEventOutcome.Success, false, false, 0, true, false, Coal));
            var presence = WorldEventClaimRules.ResolveEligibility(Inputs(WorldEventOutcome.Success, true, true, 0, true, true, Coal));
            var crate = WorldEventClaimRules.ResolveEligibility(Inputs(WorldEventOutcome.Success, true, true, 0.5, true, true, Coal));

            Assert.AreEqual(ClaimCoalReason.NoCredit, credit.CoalReason);
            Assert.AreEqual(ClaimCoalReason.NoPresence, presence.CoalReason);
            Assert.AreEqual(ClaimCoalReason.None, crate.CoalReason);
            Assert.IsNull(WorldEventClaimRules.ThresholdMessage(credit.CoalReason), "a credit coal keeps the handler's own line");
            Assert.IsNull(WorldEventClaimRules.ThresholdMessage(presence.CoalReason), "a presence coal keeps the Non Participation presence line");
        }

        [TestMethod]
        public void PresenceCoal_FellBackToTheSuccessCrate_StillStampsOne()
        {
            // The handler's fallback path: coal decided, coal create failed, the crate was paid (payCoal false).
            var payout = Run(WorldEventOutcome.Success, Inputs(WorldEventOutcome.Success, true, true, 0, false, false, Coal), Coal);

            Assert.IsTrue(payout.PayCoal);
            Assert.AreEqual((true, 1.0), WorldEventClaimRules.ResolveStamp(payout.Presence, false, payout.PaysSuccess));
        }

        [TestMethod]
        public void DefaultPolicy_IsConsolationWithNoThresholds()
        {
            Assert.AreEqual(FailureRewardMode.Consolation, WorldEventClaimPolicy.Default.OnFailure);
            Assert.AreEqual(0.0, WorldEventClaimPolicy.Default.Thresholds.MinPresenceSeconds);
            Assert.AreEqual(0.0, WorldEventClaimPolicy.Default.Thresholds.MinContributionShare);
            Assert.IsFalse(WorldEventClaimPolicy.Default.Thresholds.PresenceOn);
            Assert.IsFalse(WorldEventClaimPolicy.Default.Thresholds.ContributionOn);
            Assert.AreEqual(WorldEventClaimPolicy.Default, new WorldEventClaimPolicy(FailureRewardMode.Consolation, ClaimThresholds.None));
        }

        // ---- Thresholds ----------------------------------------------------------------------------------------

        private static WorldEventClaimPolicy Thresholds(double seconds, double share) => new WorldEventClaimPolicy(FailureRewardMode.Consolation, new ClaimThresholds(seconds, share));

        [TestMethod]
        public void PresenceThreshold_BelowPaysCoal_AtOrAbovePaysTheCrate()
        {
            var policy = Thresholds(60, 0);

            var below = Run(WorldEventOutcome.Success, Inputs(WorldEventOutcome.Success, false, false, 0, true, true, Coal, policy, secondsKnown: true, seconds: 59.9), Coal, policy);
            Assert.IsTrue(below.PayCoal);
            Assert.AreEqual(Coal, below.PayoutWcid);
            Assert.AreEqual(ClaimCoalReason.BelowPresenceThreshold, below.CoalReason);
            Assert.AreEqual("below-presence", below.Tier);
            Assert.AreEqual(" tier=below-presence", WorldEventClaimRules.PipelineLogSuffix(below, true, 0, 0));
            Assert.IsNotNull(WorldEventClaimRules.ThresholdMessage(below.CoalReason));

            var at = Run(WorldEventOutcome.Success, Inputs(WorldEventOutcome.Success, false, false, 0, true, true, Coal, policy, secondsKnown: true, seconds: 60), Coal, policy);
            Assert.IsFalse(at.PayCoal, "the threshold is a minimum: exactly 60 s qualifies");
            Assert.AreEqual(SuccessCrate, at.PayoutWcid);
            Assert.IsNull(at.Tier);
        }

        [TestMethod]
        public void ContributionThreshold_BelowPaysCoal()
        {
            var policy = Thresholds(0, 0.1);

            var below = Run(WorldEventOutcome.FailedWipe, Inputs(WorldEventOutcome.FailedWipe, false, false, 0, true, true, Coal, policy, shareKnown: true, share: 0.05), Coal, policy);
            Assert.IsTrue(below.PayCoal);
            Assert.AreEqual(ClaimCoalReason.BelowContributionThreshold, below.CoalReason);
            Assert.AreEqual("below-contribution", below.Tier);

            var above = Run(WorldEventOutcome.FailedWipe, Inputs(WorldEventOutcome.FailedWipe, false, false, 0, true, true, Coal, policy, shareKnown: true, share: 0.1), Coal, policy);
            Assert.IsFalse(above.PayCoal);
            Assert.AreEqual(ConsolationCrate, above.PayoutWcid);
        }

        [TestMethod]
        public void BothThresholds_EitherBelowPaysCoal_PresenceReportedFirst()
        {
            var policy = Thresholds(60, 0.1);

            ClaimEligibility Elig(double seconds, double share) => WorldEventClaimRules.ResolveEligibility(
                Inputs(WorldEventOutcome.Success, false, false, 0, true, true, Coal, policy, secondsKnown: true, seconds: seconds, shareKnown: true, share: share));

            Assert.AreEqual(ClaimCoalReason.BelowPresenceThreshold, Elig(10, 0.01).CoalReason, "both below");
            Assert.AreEqual(ClaimCoalReason.BelowPresenceThreshold, Elig(10, 0.5).CoalReason, "presence below");
            Assert.AreEqual(ClaimCoalReason.BelowContributionThreshold, Elig(120, 0.01).CoalReason, "contribution below");
            Assert.AreEqual(ClaimCoalReason.None, Elig(120, 0.5).CoalReason, "both met");
        }

        [TestMethod]
        [DataRow(0.0, 0.0, DisplayName = "zero is off")]
        [DataRow(-5.0, -0.1, DisplayName = "negative is off")]
        public void ZeroThresholds_AreOff(double seconds, double share)
        {
            var policy = Thresholds(seconds, share);
            var e = WorldEventClaimRules.ResolveEligibility(
                Inputs(WorldEventOutcome.Success, false, false, 0, true, true, Coal, policy, secondsKnown: true, seconds: 0, shareKnown: true, share: 0));

            Assert.IsFalse(e.PayCoal);
        }

        [TestMethod]
        public void Thresholds_FailOpen_OnUnknownInputs_AndOnAnAxisWithNoCoal()
        {
            var policy = Thresholds(60, 0.1);

            var unknown = WorldEventClaimRules.ResolveEligibility(Inputs(WorldEventOutcome.Success, false, false, 0, true, true, Coal, policy));
            Assert.IsFalse(unknown.PayCoal, "unknown seconds and share never downgrade");

            var nan = WorldEventClaimRules.ResolveEligibility(
                Inputs(WorldEventOutcome.Success, false, false, 0, true, true, Coal, policy, secondsKnown: true, seconds: double.NaN, shareKnown: true, share: double.NaN));
            Assert.IsFalse(nan.PayCoal, "NaN reads as unknown");

            var noCoal = Run(WorldEventOutcome.Success,
                Inputs(WorldEventOutcome.Success, false, false, 0, true, true, 0, policy, secondsKnown: true, seconds: 1, shareKnown: true, share: 0), 0, policy);
            Assert.IsFalse(noCoal.PayCoal, "an axis with no coal wcid has nothing to downgrade to");
            Assert.AreEqual(SuccessCrate, noCoal.PayoutWcid);
        }

        [TestMethod]
        public void ThresholdCoal_FellBackToTheCrate_StampsOne_AndLogsNoThresholdTier()
        {
            // Presence ON at 0.8 with 10 s present under a 60 s threshold: coal decided, coal create failed, crate paid.
            var policy = Thresholds(60, 0);
            var payout = Run(WorldEventOutcome.Success, Inputs(WorldEventOutcome.Success, true, true, 0.8, false, false, Coal, policy, secondsKnown: true, seconds: 10), Coal, policy);
            Assert.IsTrue(payout.PayCoal, "fixture: coal decided");

            Assert.AreEqual((true, 1.0), WorldEventClaimRules.ResolveStamp(payout.Presence, payout.PayCoal, false, payout.PaysSuccess),
                "a coal-decided claim that fell back to the crate is stamped 1.0, never the 1.6x presence multiplier");
            Assert.AreEqual((false, 0.0), WorldEventClaimRules.ResolveStamp(payout.Presence, payout.PayCoal, false, false), "a fallback consolation crate is not stamped");

            Assert.AreEqual("", WorldEventClaimRules.PipelineLogSuffix(payout, false, 0, 0), "payout=crate carries no below-presence tier");
            Assert.AreEqual(" tier=below-presence", WorldEventClaimRules.PipelineLogSuffix(payout, true, 0, 0));
            Assert.AreEqual(" presence=80 mult=1", WorldEventClaimRules.PresenceLogSuffix(payout.Presence, (true, 1.0)), "mult= reports the 1.0 actually stamped");
        }

        [TestMethod]
        public void ResolveStamp_FourArgumentForm_MatchesTheThreeArgumentForm_WhenTheDecisionIsThePresenceRules()
        {
            // Hard-coded pre-pipeline answers for the three-argument form (presence known 0.4 crate; known 0 coal).
            var crate = WorldEventClaimRules.ResolvePresencePayout(true, Coal, true, 0.4, true);
            var coal = WorldEventClaimRules.ResolvePresencePayout(true, Coal, true, 0.0, true);

            Assert.AreEqual((true, 0.8), WorldEventClaimRules.ResolveStamp(crate, false, false, true));
            Assert.AreEqual((false, 0.0), WorldEventClaimRules.ResolveStamp(coal, true, true, true));
            Assert.AreEqual((true, 1.0), WorldEventClaimRules.ResolveStamp(coal, true, false, true));
            Assert.AreEqual((false, 0.0), WorldEventClaimRules.ResolveStamp(WorldEventClaimRules.ResolvePresencePayout(false, Coal, false, 0, true), true, false, true),
                "credit rule: nothing, whatever the decision");
        }

        [TestMethod]
        public void ThresholdCoal_UnderThePresenceRule_StampsNothing()
        {
            var policy = Thresholds(60, 0);
            var payout = Run(WorldEventOutcome.Success, Inputs(WorldEventOutcome.Success, true, true, 0.8, false, false, Coal, policy, secondsKnown: true, seconds: 10), Coal, policy);

            Assert.IsTrue(payout.PayCoal);
            Assert.IsFalse(payout.Presence.PayCoal, "presence alone would have paid a 1.6x crate");
            Assert.AreEqual((false, 0.0), WorldEventClaimRules.ResolveStamp(payout.Presence, true, payout.PaysSuccess));
        }

        [TestMethod]
        public void ExistingCoal_KeepsItsReason_WhenAThresholdIsAlsoMissed()
        {
            var policy = Thresholds(60, 0.1);
            var e = WorldEventClaimRules.ResolveEligibility(
                Inputs(WorldEventOutcome.Success, false, false, 0, true, false, Coal, policy, secondsKnown: true, seconds: 0, shareKnown: true, share: 0));

            Assert.AreEqual(ClaimCoalReason.NoCredit, e.CoalReason);
            Assert.IsNull(WorldEventClaimRules.ResolvePayout(e, Reward(), WorldEventOutcome.Success, policy).Tier);
        }

        // ---- Failure reward mode -------------------------------------------------------------------------------

        [TestMethod]
        [DataRow(WorldEventOutcome.FailedTimeout)]
        [DataRow(WorldEventOutcome.FailedWipe)]
        [DataRow(WorldEventOutcome.FailedNoParticipants)]
        public void FailureModeNone_RefusesAFailedOutcome(WorldEventOutcome outcome)
        {
            var policy = new WorldEventClaimPolicy(FailureRewardMode.None, ClaimThresholds.None);

            foreach (var hasCredit in new[] { true, false })
            {
                var payout = Run(outcome, Inputs(outcome, false, false, 0, true, hasCredit, Coal, policy), Coal, policy);

                Assert.AreEqual(ClaimDenial.NoRewardForOutcome, payout.Denial);
                Assert.AreEqual(0u, payout.PayoutWcid);
                Assert.AreEqual(0, WorldEventClaimRules.ResolveExtras(payout, Reward(bonus: new List<uint> { 7 }), true).Count, "a denied claim gets no extras");
            }
        }

        [TestMethod]
        [DataRow(WorldEventOutcome.Success)]
        [DataRow(WorldEventOutcome.SuccessBossAbsent)]
        public void FailureModeNone_StillPaysASuccess(WorldEventOutcome outcome)
        {
            var policy = new WorldEventClaimPolicy(FailureRewardMode.None, ClaimThresholds.None);
            var payout = Run(outcome, Inputs(outcome, false, false, 0, true, true, Coal, policy), Coal, policy);

            Assert.AreEqual(ClaimDenial.None, payout.Denial);
            Assert.AreEqual(SuccessCrate, payout.PayoutWcid);
        }

        [TestMethod]
        public void FailureModePartial_PaysTheConsolationCrate_TaggedPartial()
        {
            var policy = new WorldEventClaimPolicy(FailureRewardMode.Partial, ClaimThresholds.None);

            var failed = Run(WorldEventOutcome.FailedTimeout, Inputs(WorldEventOutcome.FailedTimeout, false, false, 0, true, true, Coal, policy), Coal, policy);
            Assert.AreEqual(ConsolationCrate, failed.PayoutWcid);
            Assert.AreEqual("partial", failed.Tier);
            Assert.AreEqual(" tier=partial", WorldEventClaimRules.PipelineLogSuffix(failed, false, 0, 0));

            var success = Run(WorldEventOutcome.Success, Inputs(WorldEventOutcome.Success, false, false, 0, true, true, Coal, policy), Coal, policy);
            Assert.IsNull(success.Tier, "a success is never partial");
        }

        [TestMethod]
        public void NoRewardForOutcome_KeepsEveryExistingDenialToken()
        {
            // The claim line writes result=denied:<enum name>; the existing names and values must not move.
            Assert.AreEqual(0, (int)ClaimDenial.None);
            Assert.AreEqual(1, (int)ClaimDenial.NotRewarding);
            Assert.AreEqual(2, (int)ClaimDenial.AlreadyClaimedCharacter);
            Assert.AreEqual(3, (int)ClaimDenial.AlreadyClaimedAccount);
            Assert.AreEqual(4, (int)ClaimDenial.AlreadyClaimedIp);
            Assert.AreEqual(5, (int)ClaimDenial.NoRewardForOutcome);
            CollectionAssert.AreEqual(
                new[] { "None", "NotRewarding", "AlreadyClaimedCharacter", "AlreadyClaimedAccount", "AlreadyClaimedIp", "NoRewardForOutcome" },
                Enum.GetNames(typeof(ClaimDenial)));

            Assert.AreEqual("denied:NoRewardForOutcome", $"denied:{ClaimDenial.NoRewardForOutcome}");
            Assert.AreEqual("The Weave Cache holds nothing for an event that ended this way.", WorldEventClaimRules.DenialMessage(ClaimDenial.NoRewardForOutcome, null));
            Assert.AreEqual("The Glowing Chest holds nothing for an event that ended this way.", WorldEventClaimRules.DenialMessage(ClaimDenial.NoRewardForOutcome, " Glowing Chest "));
        }

        // ---- Extras --------------------------------------------------------------------------------------------

        // Columns: bonusFinalStageOnly, isFinalChainStage, coal claim  =>  extras paid.
        [TestMethod]
        [DataRow(false, false, false, true)]
        [DataRow(false, true, false, true)]
        [DataRow(true, false, false, false)]
        [DataRow(true, true, false, true)]
        [DataRow(false, false, true, false)]
        [DataRow(false, true, true, false)]
        [DataRow(true, false, true, false)]
        [DataRow(true, true, true, false)]
        public void Extras_FinalStageOnly_AndNeverOnCoal(bool finalOnly, bool isFinal, bool coalClaim, bool expectPaid)
        {
            var reward = Reward(bonus: new List<uint> { 7, 0, 8 }, finalOnly: finalOnly);
            var eligibility = WorldEventClaimRules.ResolveEligibility(Inputs(WorldEventOutcome.Success, false, false, 0, true, !coalClaim, Coal));
            var payout = WorldEventClaimRules.ResolvePayout(eligibility, reward, WorldEventOutcome.Success, null);
            Assert.AreEqual(coalClaim, payout.PayCoal, "fixture");

            var extras = WorldEventClaimRules.ResolveExtras(payout, reward, isFinal);

            if (expectPaid)
            {
                CollectionAssert.AreEqual(new uint[] { 7, 8 }, extras.Wcids.ToArray(), "zero entries are skipped");
                Assert.AreEqual(3, WorldEventClaimRules.RequiredFreeSlots(extras));
                Assert.AreEqual(" extras=2/2", WorldEventClaimRules.PipelineLogSuffix(payout, payout.PayCoal, 2, extras.Count));
            }
            else
            {
                Assert.AreEqual(0, extras.Count);
                Assert.AreEqual(1, WorldEventClaimRules.RequiredFreeSlots(extras));
                Assert.AreEqual("", WorldEventClaimRules.PipelineLogSuffix(payout, payout.PayCoal, 0, extras.Count));
            }
        }

        [TestMethod]
        public void Extras_EmptyNullOrAllZero_AreNone()
        {
            var eligibility = WorldEventClaimRules.ResolveEligibility(Inputs(WorldEventOutcome.Success, false, false, 0, true, true, Coal));
            var payout = WorldEventClaimRules.ResolvePayout(eligibility, Reward(), WorldEventOutcome.Success, null);

            Assert.AreEqual(0, WorldEventClaimRules.ResolveExtras(payout, Reward(), true).Count);
            Assert.AreEqual(0, WorldEventClaimRules.ResolveExtras(payout, new RewardDef { BonusCrateWcids = null }, true).Count);
            Assert.AreEqual(0, WorldEventClaimRules.ResolveExtras(payout, Reward(bonus: new List<uint> { 0, 0 }), true).Count);
            Assert.AreEqual(0, WorldEventClaimRules.ResolveExtras(payout, null, true).Count);
        }

        [TestMethod]
        public void Extras_PayOnAConsolationCrate_AndOnAPartialFailure()
        {
            var reward = Reward(bonus: new List<uint> { 9 });
            var policy = new WorldEventClaimPolicy(FailureRewardMode.Partial, ClaimThresholds.None);
            var payout = Run(WorldEventOutcome.FailedWipe, Inputs(WorldEventOutcome.FailedWipe, false, false, 0, true, true, Coal, policy), Coal, policy);

            CollectionAssert.AreEqual(new uint[] { 9 }, WorldEventClaimRules.ResolveExtras(payout, reward, true).Wcids.ToArray());
            Assert.AreEqual(" tier=partial extras=0/1", WorldEventClaimRules.PipelineLogSuffix(payout, false, 0, 1), "tier then extras");
        }

        [TestMethod]
        public void Extras_NeverOnAThresholdCoal()
        {
            var policy = Thresholds(60, 0);
            var reward = Reward(bonus: new List<uint> { 9 });
            var eligibility = WorldEventClaimRules.ResolveEligibility(Inputs(WorldEventOutcome.Success, false, false, 0, true, true, Coal, policy, secondsKnown: true, seconds: 1));
            var payout = WorldEventClaimRules.ResolvePayout(eligibility, reward, WorldEventOutcome.Success, policy);

            Assert.AreEqual(0, WorldEventClaimRules.ResolveExtras(payout, reward, true).Count);
        }

        // ---- RewardDef defaults and JSON -----------------------------------------------------------------------

        [TestMethod]
        public void RewardDef_BonusFields_DefaultToNone_AndReadFromJson()
        {
            var plain = JsonSerializer.Deserialize<RewardDef>(@"{ ""id"": ""standard"", ""successCrateWcid"": 1 }");
            Assert.IsNotNull(plain.BonusCrateWcids);
            Assert.AreEqual(0, plain.BonusCrateWcids.Count);
            Assert.IsFalse(plain.BonusFinalStageOnly);

            var bonus = JsonSerializer.Deserialize<RewardDef>(@"{ ""id"": ""chain"", ""bonusCrateWcids"": [5, 6], ""bonusFinalStageOnly"": true }");
            CollectionAssert.AreEqual(new uint[] { 5, 6 }, bonus.BonusCrateWcids.ToArray());
            Assert.IsTrue(bonus.BonusFinalStageOnly);
        }

        // ---- Handler wiring pins (source) ----------------------------------------------------------------------
        //
        // The handler needs a live Player, so its ORDER is pinned against the source text, like
        // WorldEventPooledLootTests.Delivery_pin_discriminates. Each pin has a discriminating negative.

        private const string HandleSignature = "private static bool Handle(WorldObject cache, Player player)";
        private const string ClaimLock = "lock (evt.ClaimedCharacters)";
        private const string Mark = "evt.ClaimedCharacters.Add(characterGuid);";
        private const string GiveExtrasCall = "GiveExtras(evt, cache, player, extras);";
        private const string FreeSlots = "player.GetFreeInventorySlots()";
        private const string Create = "WorldObjectFactory.CreateNewWorldObject(";
        private const string PrimaryGate = "if (denial == ClaimDenial.None && !roomShort)";
        private const string RoomShortBranch = "if (roomShort)";

        private static string HandleBody() => ThreadDungeons.PooledLootSourceText.MethodBody(
            ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/WorldEvents/WorldEventCacheHandler.cs"), HandleSignature);

        /// <summary>The (open, close) brace indices of the block that follows <paramref name="at"/>, or (-1, -1).</summary>
        private static (int Open, int Close) BlockAfter(string body, int at)
        {
            if (at < 0)
                return (-1, -1);

            var open = body.IndexOf('{', at);

            if (open < 0)
                return (-1, -1);

            var depth = 0;

            for (var i = open; i < body.Length; i++)
            {
                if (body[i] == '{')
                    depth++;
                else if (body[i] == '}' && --depth == 0)
                    return (open, i);
            }

            return (-1, -1);
        }

        private static int Count(string s, string token)
        {
            var n = 0;

            for (var at = s.IndexOf(token, StringComparison.Ordinal); at >= 0; at = s.IndexOf(token, at + 1, StringComparison.Ordinal))
                n++;

            return n;
        }

        /// <summary>The claim is marked before the extras are given, so an extra that throws cannot leave a paid primary unmarked.</summary>
        private static string MarkBeforeExtrasFault(string body)
        {
            if (Count(body, Mark) != 1 || Count(body, GiveExtrasCall) != 1)
                return "expected exactly one mark and one GiveExtras call";

            var lockBlock = BlockAfter(body, body.IndexOf(ClaimLock, StringComparison.Ordinal));
            var mark = body.IndexOf(Mark, StringComparison.Ordinal);
            var extras = body.IndexOf(GiveExtrasCall, StringComparison.Ordinal);

            if (lockBlock.Open < 0 || mark < lockBlock.Open || mark > lockBlock.Close || extras < lockBlock.Open || extras > lockBlock.Close)
                return "the mark and the extras must both sit inside the claim lock";

            return mark < extras ? null : "GiveExtras runs before the claim is marked";
        }

        /// <summary>The room precheck runs inside the lock before anything is created.</summary>
        private static string PrecheckBeforeCreateFault(string body)
        {
            var lockBlock = BlockAfter(body, body.IndexOf(ClaimLock, StringComparison.Ordinal));

            if (lockBlock.Open < 0)
                return "no claim lock";

            var inside = body.Substring(lockBlock.Open, lockBlock.Close - lockBlock.Open + 1);
            var check = inside.IndexOf(FreeSlots, StringComparison.Ordinal);
            var create = inside.IndexOf(Create, StringComparison.Ordinal);

            if (check < 0)
                return "no room precheck inside the claim lock";

            if (create < 0)
                return "no create inside the claim lock";

            return check < create ? null : "a create runs before the room precheck";
        }

        /// <summary>
        /// A short-room claim gives and marks nothing: the create/give/mark block is gated on !roomShort, and the
        /// roomShort branch after the lock returns before the claim line (so it can never reach a mark or a delivery).
        /// </summary>
        private static string RoomShortFault(string body)
        {
            var gate = BlockAfter(body, body.IndexOf(PrimaryGate, StringComparison.Ordinal));

            if (gate.Open < 0)
                return "the primary give is not gated on !roomShort";

            var mark = body.IndexOf(Mark, StringComparison.Ordinal);

            if (mark < gate.Open || mark > gate.Close)
                return "the mark is reachable on a short-room claim";

            var branch = BlockAfter(body, body.IndexOf(RoomShortBranch, StringComparison.Ordinal));

            if (branch.Open < 0)
                return "no roomShort branch";

            if (!body.Substring(branch.Open, branch.Close - branch.Open + 1).Contains("return true;"))
                return "the roomShort branch does not return";

            var granted = body.IndexOf("result=granted payout=", StringComparison.Ordinal);

            return granted > branch.Close ? null : "the roomShort branch is not ahead of the granted claim";
        }

        [TestMethod]
        public void Handler_MarksTheClaim_BeforeGivingExtras()
        {
            var body = HandleBody();
            Assert.IsNull(MarkBeforeExtrasFault(body));

            // Negative: the extras moved back ahead of the mark.
            var swapped = body.Replace(GiveExtrasCall, "").Replace(Mark, GiveExtrasCall + "\n" + Mark);
            Assert.IsNotNull(MarkBeforeExtrasFault(swapped), "extras ahead of the mark must fail the pin");
        }

        [TestMethod]
        public void Handler_ChecksRoom_BeforeCreatingAnything()
        {
            var body = HandleBody();
            Assert.IsNull(PrecheckBeforeCreateFault(body));

            // Negative: the precheck removed.
            Assert.IsNotNull(PrecheckBeforeCreateFault(body.Replace(FreeSlots, "0")), "no precheck must fail the pin");

            // Negative: a create hoisted ahead of the precheck.
            var gateAt = body.IndexOf("if (denial == ClaimDenial.None && extras.Count > 0)", StringComparison.Ordinal);
            Assert.IsTrue(gateAt > 0, "fixture: the precheck gate");
            var hoisted = body.Insert(gateAt, "var early = " + Create + "1u);\n");
            Assert.IsNotNull(PrecheckBeforeCreateFault(hoisted), "a create ahead of the precheck must fail the pin");
        }

        [TestMethod]
        public void Handler_ShortRoom_ReturnsBeforeAnyMark()
        {
            var body = HandleBody();
            Assert.IsNull(RoomShortFault(body));

            // Negative: the !roomShort gate dropped from the primary block.
            Assert.IsNotNull(RoomShortFault(body.Replace(PrimaryGate, "if (denial == ClaimDenial.None)")), "an ungated give must fail the pin");

            // Negative: the roomShort branch no longer returns.
            var branch = BlockAfter(body, body.IndexOf(RoomShortBranch, StringComparison.Ordinal));
            var block = body.Substring(branch.Open, branch.Close - branch.Open + 1);
            Assert.IsNotNull(RoomShortFault(body.Replace(block, block.Replace("return true;", ""))), "a non-returning branch must fail the pin");
        }

        // ---- Damage share --------------------------------------------------------------------------------------

        [TestMethod]
        public void DamageShare_IsMineOverTotal_AndUnknownWithNoDamage()
        {
            var records = new[]
            {
                new ParticipantRecord { CharacterGuid = 1, Damage = 30 },
                new ParticipantRecord { CharacterGuid = 2, Damage = 70 },
                new ParticipantRecord { CharacterGuid = 3, Damage = 0 },
                null
            };

            Assert.IsTrue(WorldEventClaimRules.TryComputeDamageShare(records, 1, out var share));
            Assert.AreEqual(0.3, share, 1e-9);

            Assert.IsTrue(WorldEventClaimRules.TryComputeDamageShare(records, 3, out share), "a contact-only claimant has a known share of 0");
            Assert.AreEqual(0.0, share);

            Assert.IsFalse(WorldEventClaimRules.TryComputeDamageShare(new[] { new ParticipantRecord { CharacterGuid = 1 } }, 1, out _), "no damage at all is unknown");
            Assert.IsFalse(WorldEventClaimRules.TryComputeDamageShare(null, 1, out _));
        }
    }
}
