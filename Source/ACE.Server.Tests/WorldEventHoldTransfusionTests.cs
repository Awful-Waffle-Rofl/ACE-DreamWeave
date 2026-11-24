using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Blood Mage Transfusion feeding World Events Hold envoys (knights). The pure demand and qualification rules, the
    /// knight selection (acceptance, alive, landblock, reach), the one queued Player-heal path a share must take, the
    /// eligibility flag, and golden shares for the fellows-and-summons split (taken from the pre-change code, which this
    /// change leaves untouched). No landblock, no PropertyManager: the run comes through FakeHoldRunView, heals through the
    /// WorldEventHoldHeals.Enqueue seam, which every test restores in a finally.
    /// </summary>
    [TestClass]
    public class WorldEventHoldTransfusionTests
    {
        private static FakeHoldRunView Run(WorldEventHoldTuning tuning) => new FakeHoldRunView(9) { Tuning = tuning };

        private static WorldEventHoldTuning HealOn => WorldEventHoldTuning.Defaults with { HealBudgetPerWave = 0, HealReductionAfterHit = 0, PlayerHealEnabled = true, PlayerHealCap = 0.06, PlayerHealMult = 1.0 };

        private static Creature Knight(IWorldEventHoldRunView run, double missingShare)
        {
            var k = HoldFixtures.Tagged(run, HoldUnitKind.Knight, name: "Sir Donovan");
            k.Health.Current = (uint)(k.Health.MaxValue * (1 - missingShare));
            return k;
        }

        // ---- pure demand --------------------------------------------------------------------------------------

        [TestMethod]
        public void KnightTransfusionDemand_IsTheRawPointsOfTheCappedShare()
        {
            // 4% of max is one typical (100 point) heal; the cap 6% binds below a 50% hole.
            Assert.AreEqual(100u, WorldEventHoldRules.KnightTransfusionDemand(0.04, 1.0, 1.0));
            Assert.AreEqual(150u, WorldEventHoldRules.KnightTransfusionDemand(0.50, 0.06, 1.0), "capped at the 6% share = 150 points");
            Assert.AreEqual(50u, WorldEventHoldRules.KnightTransfusionDemand(0.02, 0.06, 1.0), "uncapped when under the cap");
            Assert.AreEqual(200u, WorldEventHoldRules.KnightTransfusionDemand(0.04, 1.0, 0.5), "a half-value heal needs twice the points");
            Assert.AreEqual(250u, WorldEventHoldRules.KnightTransfusionDemand(0.90, 1.0, 1.0), "never above the 250 ceiling");
            Assert.AreEqual(1u, WorldEventHoldRules.KnightTransfusionDemand(0.0001, 1.0, 1.0), "a sliver still asks for a point");
        }

        [TestMethod]
        public void KnightTransfusionDemand_UnusableInputsAskForNothing()
        {
            Assert.AreEqual(0u, WorldEventHoldRules.KnightTransfusionDemand(0, 0.06, 1.0));
            Assert.AreEqual(0u, WorldEventHoldRules.KnightTransfusionDemand(0.5, 0, 1.0), "a cap of 0 refuses");
            Assert.AreEqual(0u, WorldEventHoldRules.KnightTransfusionDemand(0.5, 0.06, 0), "a mult of 0 refuses");
            Assert.AreEqual(0u, WorldEventHoldRules.KnightTransfusionDemand(double.NaN, 0.06, 1.0));
        }

        [TestMethod]
        public void KnightTransfusionDemand_MatchesTheConversionTheKnightApplies()
        {
            // The demand in raw points, converted by the knight's own rule, is the capped share of max health (to the point).
            double tokens = 0, lastAt = 0;
            var demand = WorldEventHoldRules.KnightTransfusionDemand(0.03, 1.0, 1.0);
            var healed = WorldEventHoldRules.ConvertPlayerHeal(demand, 10000, 1.0, ref tokens, ref lastAt, 1_000_000.0);
            Assert.AreEqual(300, healed);
        }

        [TestMethod]
        public void TransfusionKnightQualifies_NeedsAcceptanceLifeLandblockAndReach()
        {
            Assert.IsTrue(WorldEventHoldRules.TransfusionKnightQualifies(true, true, 4.0, 10.0));
            Assert.IsTrue(WorldEventHoldRules.TransfusionKnightQualifies(true, true, 10.0, 10.0), "reach is inclusive");
            Assert.IsFalse(WorldEventHoldRules.TransfusionKnightQualifies(true, true, 10.5, 10.0), "out of range");
            Assert.IsFalse(WorldEventHoldRules.TransfusionKnightQualifies(true, false, 4.0, 10.0), "dead");
            Assert.IsFalse(WorldEventHoldRules.TransfusionKnightQualifies(true, true, null, 10.0), "other landblock");
            Assert.IsFalse(WorldEventHoldRules.TransfusionKnightQualifies(false, true, 4.0, 10.0), "refuses player heals");
        }

        // ---- selection through the real knights -----------------------------------------------------------------

        [TestMethod]
        public void SelectTransfusionKnights_PicksOnlyAcceptingLivingKnightsInReachOnTheLandblock()
        {
            var on = Run(HealOn);
            var off = Run(HealOn with { PlayerHealEnabled = false });

            var near = Knight(on, 0.5);
            var far = Knight(on, 0.5);
            var dead = Knight(on, 0.5);
            var elsewhere = Knight(on, 0.5);
            var refuses = Knight(off, 0.5);
            var plain = HoldFixtures.MakeCreature();
            var monster = HoldFixtures.Tagged(on, HoldUnitKind.Monster);

            dead.Health.Current = 0;
            Assert.IsTrue(dead.IsDead, "sanity: a knight at 0 health is dead");

            var distance = new Dictionary<Creature, double?>
            {
                [near] = 4.0, [far] = 25.0, [dead] = 4.0, [elsewhere] = null, [refuses] = 4.0, [plain] = 4.0, [monster] = 4.0,
            };

            var picked = WorldEventHooks.SelectTransfusionKnights(distance.Keys, c => distance[c]);

            Assert.IsNotNull(picked);
            CollectionAssert.AreEqual(new[] { near }, picked, "only the hurt-or-not envoy in reach that accepts player heals");
        }

        [TestMethod]
        public void SelectTransfusionKnights_NoneQualify_IsNull()
        {
            Assert.IsNull(WorldEventHooks.SelectTransfusionKnights(new[] { HoldFixtures.MakeCreature() }, _ => 1.0));
        }

        [TestMethod]
        public void HoldTransfusionKnightDemand_FullKnightAsksForNothing_HurtKnightAsksForTheCappedShare()
        {
            var run = Run(HealOn);

            Assert.AreEqual(0u, WorldEventHooks.HoldTransfusionKnightDemand(Knight(run, 0)), "no missing health");
            Assert.AreEqual(150u, WorldEventHooks.HoldTransfusionKnightDemand(Knight(run, 0.5)), "capped at player_heal_cap 6% = 150 points");
            Assert.AreEqual(0u, WorldEventHooks.HoldTransfusionKnightDemand(HoldFixtures.MakeCreature()), "not a knight");
        }

        // ---- the payout path: a queued Player heal, never a direct write -------------------------------------------

        [TestMethod]
        public void KnightShare_GoesThroughTheQueuedPlayerHeal_AndHealsOnTheKnightsLandblock()
        {
            var saved = WorldEventHoldHeals.Enqueue;
            var queued = new List<(Creature Knight, Action Action)>();

            try
            {
                WorldEventHoldHeals.Enqueue = (k, a) => { queued.Add((k, a)); return true; };

                var run = Run(HealOn);
                var knight = Knight(run, 0.5);
                var before = knight.Health.Current;
                var demand = WorldEventHooks.HoldTransfusionKnightDemand(knight);

                // the share the payout loop hands the hook: the whole demand
                Assert.IsTrue(WorldEventHooks.TryQueuePlayerHeal(null, knight, demand, false, null, ChatMessageType.Magic));

                Assert.AreEqual(before, knight.Health.Current, "nothing is written on the caster's thread");
                Assert.AreEqual(1, queued.Count);
                Assert.AreSame(knight, queued[0].Knight);

                queued[0].Action();
                Assert.AreEqual(before + (uint)(knight.Health.MaxValue * 0.06), knight.Health.Current, "the demand heals exactly the capped 6% share");
            }
            finally
            {
                WorldEventHoldHeals.Enqueue = saved;
            }
        }

        // ---- eligibility --------------------------------------------------------------------------------------

        [TestMethod]
        public void CasterQualifies_AKnightAloneQualifiesACasterWithNobodyElse_ButEveryOtherGateStillHolds()
        {
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(true, true, true, 1, false, false), "default: unchanged");
            Assert.IsTrue(DrainSurplusEligibility.CasterQualifies(true, true, true, 1, false, false, hasHoldKnight: true));
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(true, true, true, 0, false, false, hasHoldKnight: true), "unlearned");
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(false, true, true, 1, false, false, hasHoldKnight: true), "monster caster");
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(true, true, false, 1, false, false, hasHoldKnight: true), "PvP victim");
        }

        // ---- review round: F1 over-bid, F3 read order, F4 gate ----------------------------------------------------

        [TestMethod]
        public void KnightWithAnEmptyBucket_BidsNothing_AndTheFellowTakesTheWholeShare()
        {
            var run = Run(HealOn);
            var knight = Knight(run, 0.5);

            Assert.AreEqual(150u, WorldEventHooks.HoldTransfusionKnightDemand(knight), "sanity: a fresh bucket bids the capped share");

            // drain the bucket right now
            lock (knight.HoldState)
            {
                knight.HoldState.HealTokens = 0;
                knight.HoldState.HealLastAt = ACE.Common.Time.GetUnixTime();
            }

            var demand = WorldEventHooks.HoldTransfusionKnightDemand(knight);
            Assert.AreEqual(0u, demand, "an empty bucket cannot take a heal");

            // fellow missing 100 + that knight: the fellow gets the full 100 of a 100 surplus
            CollectionAssert.AreEqual(new uint[] { 100, 0 }, DrainSurplusDistribution.Distribute(100, new uint[] { 100, demand }));
        }

        [TestMethod]
        public void KnightBid_IsCappedByWhatTheBucketAndBudgetCanTakeNow()
        {
            var run = Run(HealOn with { HealBudgetPerWave = 0.01 });
            var knight = Knight(run, 0.5);

            // wave budget 1% of max health, at 100 raw points per 4% of max
            var max = (double)knight.Health.MaxValue;
            Assert.AreEqual((uint)Math.Floor(Math.Floor(0.01 * max) / (max * 0.04) * 100), WorldEventHooks.HoldTransfusionKnightDemand(knight));

            var run2 = Run(HealOn);
            var k2 = Knight(run2, 0.5);
            lock (k2.HoldState)
            {
                k2.HoldState.HealTokens = 200;
                k2.HoldState.HealLastAt = ACE.Common.Time.GetUnixTime();
            }

            Assert.AreEqual((uint)Math.Floor(200 / (k2.Health.MaxValue * 0.04) * 100), WorldEventHooks.HoldTransfusionKnightDemand(k2), "200 tokens = 200 health");
        }

        private static void SetBucket(Creature knight, double tokens, double lastAt, double lastMonsterHitAt = 0)
        {
            lock (knight.HoldState)
            {
                knight.HoldState.HealTokens = tokens;
                knight.HoldState.HealLastAt = lastAt;
                knight.HoldState.LastMonsterHitAt = lastMonsterHitAt;
            }
        }

        private static uint RawFor(double health, double max, double factor = 1.0) => (uint)Math.Floor(health / (max * 0.04) * 100 / factor);

        private static void AssertWithin(uint expected, uint actual, string why) => Assert.IsTrue(actual >= expected && actual <= expected + 1, $"{why}: expected {expected} (or one more), got {actual}");

        [TestMethod]
        public void HoldPlayerHealRoom_BucketRefillsAtACapPerTenSeconds()
        {
            var knight = Knight(Run(HealOn), 0.5);
            var max = (double)knight.Health.MaxValue;
            var now = ACE.Common.Time.GetUnixTime();

            // empty bucket, last touched 5 s ago: floor(5 * max * cap / 10) health has refilled
            SetBucket(knight, 0, now - 5);

            var expectedHealth = Math.Floor(5 * max * 0.06 / 10);
            AssertWithin(RawFor(expectedHealth, max), WorldEventHooks.HoldTransfusionKnightDemand(knight), "half a window of refill");
        }

        [TestMethod]
        public void HoldPlayerHealRoom_AfterHitWindow_HalvesWhatAPointIsWorth_SoTheBidDoubles()
        {
            var now = ACE.Common.Time.GetUnixTime();
            var tuned = HealOn with { HealReductionAfterHit = 0.5, HealReductionSeconds = 5 };

            var plain = Knight(Run(tuned), 0.5);
            var hit = Knight(Run(tuned), 0.5);
            var max = (double)plain.Health.MaxValue;

            SetBucket(plain, 200, now);
            SetBucket(hit, 200, now, lastMonsterHitAt: now);

            var plainBid = WorldEventHooks.HoldTransfusionKnightDemand(plain);
            var hitBid = WorldEventHooks.HoldTransfusionKnightDemand(hit);

            AssertWithin(RawFor(200, max), plainBid, "no recent hit");
            AssertWithin(RawFor(200, max, 0.5), hitBid, "a hit just landed: a point is worth half");
            Assert.IsTrue(Math.Abs((double)hitBid - 2.0 * plainBid) <= 2, $"the bid doubles ({plainBid} -> {hitBid})");
        }

        [TestMethod]
        public void KnightBid_ThroughTheQueuedHeal_AppliesExactlyTheHealthItWasConvertedFrom()
        {
            var saved = WorldEventHoldHeals.Enqueue;

            try
            {
                foreach (var hitJustLanded in new[] { false, true })
                {
                    var queued = new List<Action>();
                    WorldEventHoldHeals.Enqueue = (k, a) => { queued.Add(a); return true; };

                    var now = ACE.Common.Time.GetUnixTime();
                    var knight = Knight(Run(HealOn with { HealReductionAfterHit = 0.5, HealReductionSeconds = 5 }), 0.5);
                    var max = (double)knight.Health.MaxValue;
                    var factor = hitJustLanded ? 0.5 : 1.0;

                    SetBucket(knight, 200, now, hitJustLanded ? now : 0);

                    var bid = WorldEventHooks.HoldTransfusionKnightDemand(knight);
                    var before = knight.Health.Current;

                    Assert.IsTrue(WorldEventHooks.TryQueuePlayerHeal(null, knight, bid, false, null, ChatMessageType.Magic));
                    queued[0]();

                    var applied = (int)(knight.Health.Current - before);
                    var converted = (int)Math.Floor(max * 0.04 * (bid * factor) / 100.0);

                    Assert.AreEqual(converted, applied, $"applied health equals the bid's conversion (hit={hitJustLanded})");
                    Assert.IsTrue(applied > 0 && applied <= 200, $"and fits the bucket it was sized from (applied {applied})");
                }
            }
            finally
            {
                WorldEventHoldHeals.Enqueue = saved;
            }
        }

        [TestMethod]
        public void SelectTransfusionKnights_OffLandblockOrFar_NeverReadsTheKnightsState()
        {
            var run = Run(HealOn);
            var near = Knight(run, 0.5);

            var farRun = Run(HealOn);
            var far = Knight(farRun, 0.5);

            var elsewhereRun = Run(HealOn);
            var elsewhere = Knight(elsewhereRun, 0.5);

            var picked = WorldEventHooks.SelectTransfusionKnights(new[] { near, far, elsewhere }, c => c == near ? 3.0 : c == far ? 50.0 : (double?)null);

            CollectionAssert.AreEqual(new[] { near }, picked);
            Assert.AreEqual(0, farRun.IsActiveReads, "a knight beyond reach is never asked about its run");
            Assert.AreEqual(0, elsewhereRun.IsActiveReads, "a knight on another landblock is never asked about its run");
            Assert.IsTrue(run.IsActiveReads > 0, "sanity: the probe counts reads for the one that is in reach");
        }

        [TestMethod]
        public void ActiveRunCount_TracksMarkActiveAndInactive_Once()
        {
            var before = WorldEventHoldRun.ActiveRunCount;
            var r = new WorldEventHoldRun(901);

            r.MarkActive();
            r.MarkActive();
            Assert.AreEqual(before + 1, WorldEventHoldRun.ActiveRunCount, "counted once however often it is marked");
            Assert.IsTrue(WorldEventHoldRun.AnyActive);

            r.MarkInactive();
            r.MarkInactive();
            Assert.AreEqual(before, WorldEventHoldRun.ActiveRunCount);
        }

        [TestMethod]
        public void HoldTransfusionKnights_ChecksTheActiveRunGateBeforeAnyScan()
        {
            var src = Normalized("ACE.Server/WorldEvents/WorldEventHooks.cs");
            var start = src.IndexOf("public static List<Creature> HoldTransfusionKnights(Creature caster)", StringComparison.Ordinal);
            Assert.IsTrue(start > 0);

            var gate = src.IndexOf("if (!WorldEventHoldRun.AnyActive)", start, StringComparison.Ordinal);
            var scan = src.IndexOf("GetVisibleObjectsValuesOfTypeCreature()", start, StringComparison.Ordinal);

            Assert.IsTrue(gate > start && gate < scan, "the active-run gate must precede the visible-creature scan");
        }

        private static string Normalized(string serverRelative)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldEvents", "WorldEventHooks.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "Source", serverRelative)).Replace("\r\n", "\n");
        }

        // ---- golden: the fellows-and-summons split is unchanged -------------------------------------------------

        /// <summary>
        /// Shares captured by running origin/master's DrainSurplusDistribution (ApplyShare then Distribute) BEFORE this change
        /// (fellow missing 300/100/50, one summon missing 200, surplus 400) at each rank's share. A no-Hold drain feeds the
        /// same lists into the same two calls, so these exact numbers must not move.
        /// </summary>
        [TestMethod]
        public void NoHoldDrain_PerRecipientShares_AreUnchanged()
        {
            var missing = new uint[] { 300, 100, 50, 200 };

            CollectionAssert.AreEqual(GOLDEN_R1, DrainSurplusDistribution.Distribute(DrainSurplusDistribution.ApplyShare(400, 0.4), missing));
            CollectionAssert.AreEqual(GOLDEN_R2, DrainSurplusDistribution.Distribute(DrainSurplusDistribution.ApplyShare(400, 0.7), missing));
            CollectionAssert.AreEqual(GOLDEN_R3, DrainSurplusDistribution.Distribute(DrainSurplusDistribution.ApplyShare(400, 1.0), missing));
        }

        private static readonly uint[] GOLDEN_R1 = { 74, 25, 12, 49 };
        private static readonly uint[] GOLDEN_R2 = { 129, 43, 22, 86 };
        private static readonly uint[] GOLDEN_R3 = { 185, 61, 31, 123 };
    }
}
