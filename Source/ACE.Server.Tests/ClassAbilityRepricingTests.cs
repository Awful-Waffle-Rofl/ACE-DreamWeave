using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity.Facets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The class ability reprice migration's pure half (<see cref="ClassAbilityRepricing"/>) plus source
    /// pins on its Player-bound half (Player.ApplyClassAbilityRepricing), which cannot run under this test
    /// host (Player's static initializer).
    ///
    /// READS NO PropertyManager KEY, so it needs no seeding and passes alone or in any order.
    /// </summary>
    [TestClass]
    public class ClassAbilityRepricingTests
    {
        private static readonly ClassAbilityId[] Unlearned =
        {
            ClassAbilityId.Hemomancy, ClassAbilityId.WeakenedBlood, ClassAbilityId.Exsanguinate,
            ClassAbilityId.CrimsonHarvest, ClassAbilityId.HealBoostRating,
        };

        private static readonly ClassAbilityId[] Kept =
        {
            ClassAbilityId.SanguineWard, ClassAbilityId.BloodPrice, ClassAbilityId.SanguineReserve,
        };

        private static IReadOnlyList<RepriceWave> W => ClassAbilityRepricing.Waves;

        private static RepriceRow Row(ClassAbilityId id) =>
            W.Single(w => w.Number == 1).Rows.Single(r => r.Ability == id);

        // ---- the wave table ------------------------------------------------------------------------

        [TestMethod]
        public void Wave1_UnlearnsTheFiveChangedAbilities_KeepsTheOtherThree_AndPinsTheOldCosts()
        {
            var wave = W.Single(w => w.Number == 1);

            Assert.AreEqual(8, wave.Rows.Count, "wave 1 names all eight Blood Mage abilities");
            Assert.AreEqual(8, wave.Rows.Select(r => r.Ability).Distinct().Count(), "each ability once");

            foreach (var id in Unlearned)
                Assert.AreEqual(RepriceAction.Unlearn, Row(id).Action, $"{id} is functionally changed and must be unlearned");

            foreach (var id in Kept)
                Assert.AreEqual(RepriceAction.Keep, Row(id).Action, $"{id} keeps its ranks");

            // the CostPerRank values on master before this pass (ff32e4480)
            CollectionAssert.AreEqual(new[] { 1, 1, 1, 1, 1 }, Row(ClassAbilityId.Hemomancy).OldCosts.ToArray());
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, Row(ClassAbilityId.WeakenedBlood).OldCosts.ToArray());
            CollectionAssert.AreEqual(new[] { 3 }, Row(ClassAbilityId.Exsanguinate).OldCosts.ToArray());
            CollectionAssert.AreEqual(new[] { 3 }, Row(ClassAbilityId.CrimsonHarvest).OldCosts.ToArray());
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, Row(ClassAbilityId.HealBoostRating).OldCosts.ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, Row(ClassAbilityId.SanguineWard).OldCosts.ToArray());
            CollectionAssert.AreEqual(new[] { 2, 2, 2 }, Row(ClassAbilityId.BloodPrice).OldCosts.ToArray());
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, Row(ClassAbilityId.SanguineReserve).OldCosts.ToArray());

            Assert.AreEqual("Blood Mage abilities", wave.Subject);
            Assert.AreEqual("the Blood Mage trainer", wave.Trainer);
        }

        [TestMethod]
        public void NewestWaveNamingAnAbility_MatchesItsLiveCostPerRank()
        {
            var newest = new Dictionary<ClassAbilityId, RepriceRow>();

            foreach (var wave in W.OrderBy(w => w.Number))
                foreach (var row in wave.Rows)
                    newest[row.Ability] = row;

            Assert.IsTrue(newest.Count > 0);

            foreach (var (id, row) in newest)
            {
                Assert.IsTrue(ClassAbilityRegistry.Abilities.TryGetValue(id, out var def), $"{id} is not registered");

                CollectionAssert.AreEqual(def.CostPerRank, row.NewCosts.ToArray(),
                    $"{def.Name}: the newest wave's new costs must be the live CostPerRank");
            }
        }

        [TestMethod]
        public void Waves_AreNumberedFromOneAscendingAndUnique()
        {
            var numbers = W.Select(w => w.Number).ToArray();

            CollectionAssert.AreEqual(numbers.OrderBy(n => n).ToArray(), numbers, "waves must be listed in ascending order");
            Assert.AreEqual(numbers.Length, numbers.Distinct().Count(), "wave numbers must be unique");
            Assert.IsTrue(numbers.All(n => n >= 1), "wave 0 is the absent-stamp value and can never be a wave");
            Assert.AreEqual(numbers.Max(), ClassAbilityRepricing.LatestWave);
        }

        // ---- unlearns ------------------------------------------------------------------------------

        [TestMethod]
        public void PlanUnlearns_OwnedUnlearnAbilitiesOnly_WithTheirOldCost()
        {
            var ranks = new Dictionary<ClassAbilityId, int>
            {
                [ClassAbilityId.Hemomancy] = 5,
                [ClassAbilityId.Exsanguinate] = 1,
                [ClassAbilityId.SanguineWard] = 3,   // kept: never unlearned
            };

            var steps = ClassAbilityRepricing.PlanUnlearns(0, RankOf(ranks));

            Assert.AreEqual(2, steps.Count, "only OWNED abilities the wave UNLEARNS");
            Assert.AreEqual(ClassAbilityId.Hemomancy, steps[0].Ability);
            Assert.AreEqual(5, steps[0].OldRank);
            Assert.AreEqual(5, steps[0].OldPaid);
            Assert.AreEqual(ClassAbilityId.Exsanguinate, steps[1].Ability);
            Assert.AreEqual(1, steps[1].OldRank);
            Assert.AreEqual(3, steps[1].OldPaid, "Exsanguinate's single old rank cost 3");
        }

        [TestMethod]
        public void UnlearnedAbilityNames_AreTheDefNamesFacetBuildsAreKeyedBy()
        {
            var names = ClassAbilityRepricing.UnlearnedAbilityNames(W, 0);

            CollectionAssert.AreEquivalent(
                Unlearned.Select(id => ClassAbilityRegistry.Abilities[id].Name).ToArray(), names.ToArray());

            Assert.AreEqual(0, ClassAbilityRepricing.UnlearnedAbilityNames(W, ClassAbilityRepricing.LatestWave).Count);
        }

        // ---- the points recompute and its floor ------------------------------------------------------

        [TestMethod]
        public void RecomputePoints_IsEarnedMinusOwnedMinusOutstandingTokens_WhenItBeatsTheFloor()
        {
            var p = ClassAbilityRepricing.RecomputePoints(totalEarned: 30, currentAvailable: 4, ownedCostAfter: 12, outstandingTokenValue: 2, oldPriceRefund: 10);

            Assert.AreEqual(16, p.Recomputed);
            Assert.AreEqual(14, p.Floor);
            Assert.AreEqual(16, p.NewAvailable, "the recompute wins when the character was owed more than the old-price refund");
            Assert.AreEqual(12, p.Delta);
            Assert.IsFalse(p.FloorBinds);
        }

        [TestMethod]
        public void RecomputePoints_TheFloorIsCurrentPlusTheOldPriceRefund()
        {
            var p = ClassAbilityRepricing.RecomputePoints(totalEarned: 20, currentAvailable: 10, ownedCostAfter: 12, outstandingTokenValue: 1, oldPriceRefund: 3);

            Assert.AreEqual(7, p.Recomputed);
            Assert.AreEqual(13, p.Floor);
            Assert.AreEqual(3, p.OldPriceRefund);
            Assert.AreEqual(13, p.NewAvailable, "nobody loses the refund the old prices owe them");
            Assert.AreEqual(3, p.Delta);
            Assert.IsTrue(p.FloorBinds, "the floor won, so the caller must log it");

            // the boundary: equal is not binding
            Assert.IsFalse(ClassAbilityRepricing.RecomputePoints(20, 4, 12, 1, 3).FloorBinds);

            // a negative refund (a price RISE on a kept rank) is clamped, so a balance is never lowered
            var rise = ClassAbilityRepricing.RecomputePoints(20, 10, 12, 1, -5);
            Assert.AreEqual(10, rise.Floor);
            Assert.AreEqual(10, rise.NewAvailable);
            Assert.AreEqual(0, rise.Delta);
        }

        [TestMethod]
        public void TheWorkedExample_AFullBloodMage()
        {
            // Hemomancy 5, Weakened Blood 3, Exsanguinate 1, Crimson Harvest 1, Heal Boost Rating 3,
            // Sanguine Ward 3, Blood Price 3, Sanguine Reserve 3, all paid at the old prices, no points left.
            var ranks = new Dictionary<ClassAbilityId, int>
            {
                [ClassAbilityId.Hemomancy] = 5, [ClassAbilityId.WeakenedBlood] = 3, [ClassAbilityId.Exsanguinate] = 1,
                [ClassAbilityId.CrimsonHarvest] = 1, [ClassAbilityId.HealBoostRating] = 3, [ClassAbilityId.SanguineWard] = 3,
                [ClassAbilityId.BloodPrice] = 3, [ClassAbilityId.SanguineReserve] = 3,
            };

            var c = Simulate(ranks, new List<(int, int, int)>(), availableBefore: 0, extraOwnedPaid: 0);

            // paid 5 + 3 + 3 + 3 + 3 + 6 + 6 + 3 = 32; keeps Ward 3 + Price 3 + Reserve 3 = 9 at the new prices
            Assert.AreEqual(32, c.Total);
            Assert.AreEqual(9, c.OwnedAfter);
            Assert.AreEqual(23, c.Points.NewAvailable);
            Assert.AreEqual(23, c.Points.Delta, "17 for the unlearned ranks + 3 Ward + 3 Price price drops");
            Assert.AreEqual(23, c.Expected);
        }

        /// <summary>
        /// For EVERY combination of owned ranks (0, 1 or top of the old ladder for each of the eight) plus a
        /// fixed carried-token set and an unrelated owned ability, a character whose balance was consistent
        /// before the wave ends with exactly the pinned-old-price expectation, and the CAP audit identity
        /// (with its sink term equal to the token value still outstanding, which is what CapLedger.SinkSpend now
        /// derives from the ledger - purchases net of refunds and take-backs - for tokens bought on the ledger)
        /// reads zero unexplained afterwards. ProductionAudit_* below run the real SinkSpend over real rows.
        /// </summary>
        [TestMethod]
        public void EveryRankCombination_RecomputeEqualsTheOldPriceExpectation_AndTheAuditReadsZero()
        {
            var ids = Unlearned.Concat(Kept).ToArray();
            var other = OtherAbility();

            var tokens = new List<(int TokenId, int Tier, int Stack)>
            {
                ((int)ClassAbilityId.SanguineWard, 1, 1),   // unchanged price: kept, still outstanding
                ((int)ClassAbilityId.SanguineWard, 2, 1),   // 2 -> 1: taken back
                ((int)ClassAbilityId.Hemomancy, 1, 1),      // unlearned ability: taken back
                ((int)ClassAbilityId.BloodPrice, 3, 2),     // 2 -> 1, a stack of two: taken back
                ((int)other.Id, 1, 1),                      // unrelated: untouched
            };

            var combos = 0;

            foreach (var choice in Choices(ids.Length))
            {
                var ranks = new Dictionary<ClassAbilityId, int> { [other.Id] = 1 };

                for (var i = 0; i < ids.Length; i++)
                {
                    var oldLadder = Row(ids[i]).OldCosts.Count;
                    ranks[ids[i]] = choice[i] == 0 ? 0 : choice[i] == 1 ? 1 : oldLadder;
                }

                // (balance before, pre-existing error): consistent at 0 and 7, overcredited by 5, owed 3
                foreach (var (availableBefore, overcredit) in new[] { (0, 0), (7, 0), (7, 5), (7, -3) })
                {
                    var c = Simulate(ranks, tokens, availableBefore, extraOwnedPaid: 0, overcredit: overcredit);
                    var at = $"combination {string.Join(",", choice)}, overcredit {overcredit}";

                    Assert.IsTrue(c.Expected >= 0, $"{at}: every wave-1 price fell or held");

                    // nobody ever loses the old-price refund; an owed character also gets what it was owed
                    Assert.AreEqual(c.Expected + Math.Max(0, -overcredit), c.Points.Delta, $"{at}: delta");
                    Assert.AreEqual(overcredit > 0, c.Points.FloorBinds, $"{at}: the floor wins exactly when the balance was overcredited");

                    // the audit identity (sink term = outstanding token value, as SinkSpend now nets it) reads zero unless the floor won,
                    // and then exactly the overcredit the character already held
                    Assert.AreEqual(overcredit > 0 ? -overcredit : 0,
                        CapLedger.Unexplained(c.Total, c.Points.NewAvailable, c.OwnedAfter, c.OutstandingValue), $"{at}: audit");

                    combos++;
                }
            }

            Assert.AreEqual(3 * 3 * 3 * 3 * 3 * 3 * 3 * 3 * 4, combos);
        }

        [TestMethod]
        public void TheFloor_WhenItBinds_StillPaysTheOldPriceRefund_AndLeavesTheOvercreditVisible()
        {
            // the balance was ALREADY 5 above what the earnings explain (an admin credit, say)
            var ranks = new Dictionary<ClassAbilityId, int> { [ClassAbilityId.BloodPrice] = 1, [ClassAbilityId.Exsanguinate] = 1 };

            var c = Simulate(ranks, new List<(int, int, int)>(), availableBefore: 10, extraOwnedPaid: 0, overcredit: 5);

            // earned 15 (10 + Blood Price 2 + Exsanguinate 3); recompute = 15 - owned 1 = 14 versus the floor
            // current 15 + refund (Exsanguinate 3 + Blood Price 2 - 1) 4 = 19
            Assert.AreEqual(14, c.Points.Recomputed);
            Assert.AreEqual(4, c.Points.OldPriceRefund);
            Assert.IsTrue(c.Points.FloorBinds);
            Assert.AreEqual(19, c.Points.NewAvailable);
            Assert.AreEqual(4, c.Points.Delta, "the unlearn refund is paid even though the recompute came out low");
            Assert.AreEqual(c.Points.Recomputed - c.Points.NewAvailable,
                CapLedger.Unexplained(c.Total, c.Points.NewAvailable, c.OwnedAfter, c.OutstandingValue),
                "a binding floor shows up in the audit as exactly recompute minus the floored balance");
        }

        // ---- idempotency and the stamp ---------------------------------------------------------------

        [TestMethod]
        public void ReRunAfterSuccess_IsANoOp_AndACrashMidwayConvergesOnTheSameBalance()
        {
            var ranks = new Dictionary<ClassAbilityId, int>
            {
                [ClassAbilityId.Exsanguinate] = 1, [ClassAbilityId.SanguineWard] = 3, [ClassAbilityId.Hemomancy] = 2,
            };
            var tokens = new List<(int TokenId, int Tier, int Stack)> { ((int)ClassAbilityId.BloodPrice, 1, 1), ((int)ClassAbilityId.SanguineWard, 1, 1) };

            var first = Simulate(ranks, tokens, availableBefore: 3, extraOwnedPaid: 0);

            // the stamp was lost but everything else persisted: nothing left to unlearn or take back, delta 0
            Assert.AreEqual(0, ClassAbilityRepricing.PlanUnlearns(0, RankOf(first.RanksAfter)).Count);
            Assert.IsFalse(first.Outstanding.Any(t => ClassAbilityRepricing.DecideTokenRefund(0, t.TokenId, t.Tier, t.Stack).Refund));

            Assert.AreEqual(first.Total - first.OwnedAfter - first.OutstandingValue, first.Points.NewAvailable,
                "a migrated character's balance IS the recompute, so the audit reads it as settled");

            // a crash after the ranks and tokens persisted but before the points did: the re-run starts from
            // the OLD balance and the already-erased ranks (its old-price refund no longer sees the unlearned
            // ranks or the consumed token), and still lands on the same final balance
            var refundAfterCrash = ClassAbilityRepricing.ExpectedDelta(W, 0, RankOf(first.RanksAfter), 0);
            Assert.IsTrue(refundAfterCrash < first.Expected, "control: the floor really is smaller after the crash");

            var crashed = ClassAbilityRepricing.RecomputePoints(first.Total, 3, first.OwnedAfter, first.OutstandingValue, refundAfterCrash);
            Assert.AreEqual(first.Points.NewAvailable, crashed.NewAvailable);
            Assert.IsFalse(crashed.FloorBinds);
        }

        [TestMethod]
        public void StampedAtTheLatestWave_EveryDecisionIsANoOp()
        {
            var latest = ClassAbilityRepricing.LatestWave;
            Func<ClassAbilityId, int> maxed = _ => 3;

            Assert.AreEqual(0, ClassAbilityRepricing.PlanUnlearns(latest, maxed).Count);
            Assert.IsFalse(ClassAbilityRepricing.OwnsAnyPendingAbility(W, latest, maxed));
            Assert.AreEqual(0, ClassAbilityRepricing.UnlearnedAbilityNames(W, latest).Count);

            foreach (var id in Unlearned.Concat(Kept))
            {
                Assert.IsFalse(ClassAbilityRepricing.DecideTokenRefund(latest, (int)id, 1, 1).Refund, $"{id}: token after stamp");
                Assert.IsFalse(ClassAbilityRepricing.IsAbilityRepricePending(latest, id), $"{id}: pending after stamp");
            }

            // control: the same character unstamped IS migrated
            Assert.AreEqual(5, ClassAbilityRepricing.PlanUnlearns(0, maxed).Count);
            Assert.IsTrue(ClassAbilityRepricing.OwnsAnyPendingAbility(W, 0, maxed));
        }

        [TestMethod]
        public void OwnsAnyPendingAbility_OnlyForOwnersOfAWaveAbility()
        {
            var other = OtherAbility();

            Assert.IsFalse(ClassAbilityRepricing.OwnsAnyPendingAbility(W, 0, id => id == other.Id ? 3 : 0),
                "a character owning only unrelated abilities is not recomputed");
            Assert.IsTrue(ClassAbilityRepricing.OwnsAnyPendingAbility(W, 0, id => id == ClassAbilityId.SanguineReserve ? 1 : 0),
                "a KEPT ability counts");
        }

        // ---- carried tokens ------------------------------------------------------------------------

        [TestMethod]
        public void DecideTokenRefund_UnlearnedAbilities_EveryOldTierAtItsOldPrice()
        {
            for (var t = 1; t <= 5; t++) AssertToken(ClassAbilityId.Hemomancy, t, true, 1);
            for (var t = 1; t <= 3; t++) AssertToken(ClassAbilityId.WeakenedBlood, t, true, 1);
            for (var t = 1; t <= 3; t++) AssertToken(ClassAbilityId.HealBoostRating, t, true, 1);
            AssertToken(ClassAbilityId.Exsanguinate, 1, true, 3);
            AssertToken(ClassAbilityId.CrimsonHarvest, 1, true, 3);

            // a tier the OLD ladder never had could not have been bought at the old price
            AssertToken(ClassAbilityId.Exsanguinate, 2, false, 0);
            AssertToken(ClassAbilityId.CrimsonHarvest, 3, false, 0);

            Assert.IsTrue(ClassAbilityRepricing.DecideTokenRefund(0, (int)ClassAbilityId.Hemomancy, 1, 1).Unlearned);
        }

        [TestMethod]
        public void DecideTokenRefund_KeptAbilities_OnlyAChangedTierPrice()
        {
            AssertToken(ClassAbilityId.SanguineWard, 1, false, 0);   // 1 -> 1
            AssertToken(ClassAbilityId.SanguineWard, 2, true, 2);
            AssertToken(ClassAbilityId.SanguineWard, 3, true, 3);

            AssertToken(ClassAbilityId.BloodPrice, 1, true, 2);
            AssertToken(ClassAbilityId.BloodPrice, 2, true, 2);
            AssertToken(ClassAbilityId.BloodPrice, 3, true, 2);

            for (var t = 1; t <= 3; t++) AssertToken(ClassAbilityId.SanguineReserve, t, false, 0);   // unchanged

            Assert.IsFalse(ClassAbilityRepricing.DecideTokenRefund(0, (int)ClassAbilityId.BloodPrice, 1, 1).Unlearned);

            // stacks, a stack below 1, not a token, an unrelated ability
            var stack = ClassAbilityRepricing.DecideTokenRefund(0, (int)ClassAbilityId.BloodPrice, 1, 2);
            Assert.AreEqual(2, stack.Stack);
            Assert.AreEqual(4, stack.Total);
            Assert.AreEqual(2, ClassAbilityRepricing.DecideTokenRefund(0, (int)ClassAbilityId.BloodPrice, 1, 0).Total);
            Assert.IsFalse(ClassAbilityRepricing.DecideTokenRefund(0, 0, 1, 1).Refund);
            Assert.IsFalse(ClassAbilityRepricing.DecideTokenRefund(0, (int)OtherAbility().Id, 1, 1).Refund);
        }

        [TestMethod]
        public void TokenValue_IsTheExchangerRefund()
        {
            var bp = ClassAbilityRegistry.Abilities[ClassAbilityId.BloodPrice];

            Assert.AreEqual(bp.CostPerRank[0], ClassAbilityRepricing.TokenValue((int)ClassAbilityId.BloodPrice, 1, 1));
            Assert.AreEqual(bp.CostPerRank[0] * 3, ClassAbilityRepricing.TokenValue((int)ClassAbilityId.BloodPrice, 1, 3));
            Assert.AreEqual(0, ClassAbilityRepricing.TokenValue((int)ClassAbilityId.BloodPrice, bp.CostPerRank.Length + 1, 1), "a tier off the ladder is unrefundable");
            Assert.AreEqual(0, ClassAbilityRepricing.TokenValue(0, 1, 1), "not a token");
        }

        // ---- stored facet builds -----------------------------------------------------------------------

        [TestMethod]
        public void TryStripAbilities_RemovesOnlyTheNamedKeys()
        {
            var json = FacetSnapshot.SerializeAbilities(new Dictionary<string, int> { ["hemomancy"] = 3, ["sanguine_ward"] = 2 });

            Assert.IsTrue(ClassAbilityRepricing.TryStripAbilities(json, new[] { "hemomancy", "exsanguinate" }, out var stripped, out var removed));
            CollectionAssert.AreEqual(new[] { "hemomancy" }, removed);

            var left = FacetSnapshot.DeserializeAbilities(stripped);
            Assert.AreEqual(1, left.Count);
            Assert.AreEqual(2, left["sanguine_ward"], "a kept ability keeps its stored rank");

            // nothing named: no rewrite
            Assert.IsFalse(ClassAbilityRepricing.TryStripAbilities(stripped, new[] { "hemomancy" }, out var same, out _));
            Assert.AreEqual(stripped, same);

            // an unreadable blob is never replaced
            Assert.IsFalse(ClassAbilityRepricing.TryStripAbilities("not json", new[] { "hemomancy" }, out var bad, out _));
            Assert.AreEqual("not json", bad);
        }

        [TestMethod]
        public void TryStripStoredAbilities_WritesOnlyChangedRows_AndReportsWhatWasRemoved()
        {
            var a = Facet(1, new Dictionary<string, int> { ["hemomancy"] = 5, ["blood_price"] = 3 });
            var b = Facet(2, new Dictionary<string, int> { ["blood_price"] = 1 });
            var writes = 0;

            var ok = ClassAbilityRepricing.TryStripStoredAbilities(new[] { a, b }, new[] { "hemomancy" }, 50,
                (r, cb) => { writes++; cb(true); }, out var rows, out var names);

            Assert.IsTrue(ok);
            Assert.AreEqual(1, rows);
            Assert.AreEqual(1, writes, "an untouched row is not written");
            CollectionAssert.AreEqual(new[] { "hemomancy" }, names.ToArray());
            Assert.AreEqual("[{\"s\":1}]", a.SkillsJson, "skills_Json must not be touched");
            Assert.IsFalse(FacetSnapshot.DeserializeAbilities(a.AbilitiesJson).ContainsKey("hemomancy"));
        }

        [TestMethod]
        public void TryStripStoredAbilities_AnUnconfirmedWrite_ReturnsFalseSoTheWaveIsNotStamped()
        {
            var never = ClassAbilityRepricing.TryStripStoredAbilities(
                new[] { Facet(1, new Dictionary<string, int> { ["hemomancy"] = 1 }) }, new[] { "hemomancy" }, 50,
                (r, cb) => { /* never confirms */ }, out var rows, out _);

            Assert.IsFalse(never);
            Assert.AreEqual(0, rows);

            Assert.IsFalse(ClassAbilityRepricing.TryStripStoredAbilities(
                new[] { Facet(1, new Dictionary<string, int> { ["hemomancy"] = 1 }) }, new[] { "hemomancy" }, 50,
                (r, cb) => cb(false), out _, out _));

            // control: no rows at all is a success with nothing written
            Assert.IsTrue(ClassAbilityRepricing.TryStripStoredAbilities(new CharacterFacet[0], new[] { "hemomancy" }, 50,
                (r, cb) => cb(false), out _, out _));
        }

        // ---- the chat line ---------------------------------------------------------------------------

        [TestMethod]
        public void NoticeLines_TheOwnerSpecifiedLine()
        {
            var all = new[] { "Hemomancy", "Weakened Blood", "Exsanguinate", "Crimson Harvest", "Heal Boost Rating" };

            var lines = ClassAbilityRepricing.BuildNoticeLines("Blood Mage abilities", "the Blood Mage trainer", all, 12, 0, null);

            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual("Blood Mage abilities were reworked: Hemomancy, Weakened Blood, Exsanguinate, Crimson Harvest and Heal Boost Rating have been unlearned and 12 class ability points returned. Visit the Blood Mage trainer to re-learn them.", lines[0]);

            var one = ClassAbilityRepricing.BuildNoticeLines("Blood Mage abilities", "the Blood Mage trainer", new[] { "Exsanguinate" }, 1, 0, null);
            Assert.AreEqual("Blood Mage abilities were reworked: Exsanguinate has been unlearned and 1 class ability point returned. Visit the Blood Mage trainer to re-learn it.", one.Single());

            var zero = ClassAbilityRepricing.BuildNoticeLines("Blood Mage abilities", "the Blood Mage trainer", new[] { "Hemomancy" }, 0, 0, null);
            Assert.AreEqual("Blood Mage abilities were reworked: Hemomancy has been unlearned. Visit the Blood Mage trainer to re-learn it.", zero.Single());

            var keptOnly = ClassAbilityRepricing.BuildNoticeLines("Blood Mage abilities", "the Blood Mage trainer", new string[0], 3, 0, null);
            Assert.AreEqual("Blood Mage abilities were reworked and repriced: 3 class ability points returned.", keptOnly.Single());

            Assert.AreEqual(0, ClassAbilityRepricing.BuildNoticeLines("Blood Mage abilities", "the Blood Mage trainer", new string[0], 0, 0, null).Count,
                "nothing visible happened: no line");
        }

        [TestMethod]
        public void NoticeLines_TokensAndFacetsGoOnASecondLine_AndEveryLineFitsTheChatLimit()
        {
            var all = new[] { "Hemomancy", "Weakened Blood", "Exsanguinate", "Crimson Harvest", "Heal Boost Rating" };

            var lines = ClassAbilityRepricing.BuildNoticeLines("Blood Mage abilities", "the Blood Mage trainer", all, 1234, 12, all);

            Assert.AreEqual(2, lines.Count);
            StringAssert.Contains(lines[1], "12 unused training tokens were taken back");
            StringAssert.Contains(lines[1], "Your stored facet builds no longer hold Hemomancy, Weakened Blood, Exsanguinate, Crimson Harvest and Heal Boost Rating.");

            var stripOnly = ClassAbilityRepricing.BuildNoticeLines("Blood Mage abilities", "the Blood Mage trainer", new string[0], 0, 0, new[] { "Hemomancy" });
            Assert.AreEqual("Blood Mage abilities were reworked: Hemomancy has been removed from your stored facet builds. Visit the Blood Mage trainer to re-learn it.", stripOnly.Single());

            foreach (var line in lines.Concat(stripOnly))
            {
                Assert.IsTrue(line.Length <= 250, $"chat lines are cut at 255 characters: {line.Length} in \"{line}\"");
                Assert.IsTrue(line.All(ch => ch < 128), $"ASCII only: {line}");
            }
        }

        [TestMethod]
        public void JoinNames_EnglishList()
        {
            Assert.AreEqual("", ClassAbilityRepricing.JoinNames(new string[0]));
            Assert.AreEqual("A", ClassAbilityRepricing.JoinNames(new[] { "A" }));
            Assert.AreEqual("A and B", ClassAbilityRepricing.JoinNames(new[] { "A", "B" }));
            Assert.AreEqual("A, B and C", ClassAbilityRepricing.JoinNames(new[] { "A", "B", "C" }));
        }

        // ---- pending-wave refusal --------------------------------------------------------------------

        [TestMethod]
        public void IsAbilityRepricePending_CoversAllEightBloodMageAbilities()
        {
            foreach (var id in Unlearned.Concat(Kept))
            {
                Assert.IsTrue(ClassAbilityRepricing.IsAbilityRepricePending(0, id), $"{id}: unmigrated character, wave 1 pending");
                Assert.IsFalse(ClassAbilityRepricing.IsAbilityRepricePending(ClassAbilityRepricing.LatestWave, id), $"{id}: migrated character");
            }

            Assert.IsFalse(ClassAbilityRepricing.IsAbilityRepricePending(0, OtherAbility().Id), "an ability no wave names is never pending");

            var waves = new[]
            {
                new RepriceWave(1, "a", "s", "t", new RepriceRow(ClassAbilityId.BloodPrice, new[] { 2 }, new[] { 1 }, RepriceAction.Keep)),
                new RepriceWave(2, "b", "s", "t", new RepriceRow(ClassAbilityId.SanguineWard, new[] { 2 }, new[] { 1 }, RepriceAction.Keep)),
            };

            Assert.IsFalse(ClassAbilityRepricing.IsAbilityRepricePending(waves, 1, ClassAbilityId.BloodPrice), "wave 1 already applied");
            Assert.IsTrue(ClassAbilityRepricing.IsAbilityRepricePending(waves, 1, ClassAbilityId.SanguineWard), "wave 2 still pending");
        }

        [TestMethod]
        public void PendingRefusal_IsCheckedOnEveryRankAndTokenPath()
        {
            var abilities = Source("Player_ClassAbilities.cs");
            var tokens = Source("Player_ClassAbilityTokens.cs");

            AssertGuarded(abilities, "private bool ValidateClassAbilityRankPrerequisites(", "private bool ApplyClassAbilityRankCore(");
            AssertGuarded(tokens, "public bool CanBuyClassAbilityToken(", "private bool TryFindClassAbilityVoucher(");
            AssertGuarded(tokens, "public bool RefundUnusedVoucher(", "private bool TryRefundRetiredVoucher(");
            AssertGuarded(abilities, "public bool UnlearnClassAbility(", "refunded = skill.CumulativeCost(rank);");

            // the trainer's full respec charges its fee before unlearning, so it must refuse up front
            var trainer = File.ReadAllText(Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(),
                "Source", "ACE.Server", "ClassAbilities", "ClassAbilityTrainer.cs"));
            var pending = trainer.IndexOf("player.ClassAbilityRepricePendingFor(entry.skill.Id)", StringComparison.Ordinal);
            var fee = trainer.IndexOf("player.TrySpendLuminanceIncludingBank(currentFee)", StringComparison.Ordinal);
            Assert.IsTrue(pending >= 0 && pending < fee, "the full respec must refuse a pending reprice before it charges the fee");

            Assert.AreEqual("Your class ability price update is still being applied - try again in a moment, or log in again if this persists.", ClassAbilityRepricing.PendingRefusal);
        }

        // ---- source pins on Player.ApplyClassAbilityRepricing --------------------------------------

        [TestMethod]
        public void Login_RunsRepricingAfterTheOverhaulRespecAndBeforeTheAudit()
        {
            var text = Source("Player_Networking.cs");

            var respec = text.IndexOf("ApplyClassAbilityOverhaulRespec();", StringComparison.Ordinal);
            var reprice = text.IndexOf("var capAuditDeferred = ApplyClassAbilityRepricing();", StringComparison.Ordinal);
            var gate = text.IndexOf("if (!capAuditDeferred)", StringComparison.Ordinal);
            var audit = text.IndexOf("AuditClassAbilityPointLedger();", StringComparison.Ordinal);

            Assert.IsTrue(respec >= 0 && reprice >= 0 && gate >= 0 && audit >= 0, "one of the login calls is missing");
            Assert.IsTrue(respec < reprice, "repricing must run after the overhaul respec");
            Assert.IsTrue(reprice < gate && gate < audit, "the login audit must run after repricing, and only when repricing did not defer it");
        }

        [TestMethod]
        public void ApplyClassAbilityRepricing_RunsTheLoginSequenceInOrder()
        {
            var body = ApplyBody();

            var order = new[]
            {
                "if (appliedWave >= latestWave)",
                "\"class_ability_reprice_sweep_enabled\"",
                "if (unlearnNames.Count > 0 && TotalClassAbilityPointsEarned > 0)",
                "TryGetCharacterFacetRows(out var facetRows)",
                "ClassAbilityRepricing.TryStripStoredAbilities(",
                "ranksBefore[row.Ability] =",
                "Character.EraseQuest(",
                "WriteRepriceMarker(latestWave,",
                "SaveCharacterToDatabase(saved =>",
                "private void FinishClassAbilityRepricing(",
                "ReadRepriceMarkers(appliedWave, latestWave,",
                "ClassAbilityRepricing.DecideTokenRefund(waves, appliedWave,",
                "TryConsumeFromInventoryWithNetworking(",
                "ClassAbilityRepricing.RecomputePoints(",
                "AdjustClassAbilityPoints(netDelta, 0, CapLedgerReason.Reprice",
                "SetProperty(PropertyInt.ClassAbilityRepriceWave, latestWave)",
                "if (!touched && run.StrippedFacets == 0)",
                "SaveBiotaToDatabase();",
            };

            var at = -1;
            foreach (var marker in order)
            {
                var next = body.IndexOf(marker, at + 1, StringComparison.Ordinal);
                Assert.IsTrue(next > at, $"'{marker}' is missing or out of order in ApplyClassAbilityRepricing");
                at = next;
            }

            Assert.AreEqual(1, CountOf(body, "SaveCharacterToDatabase("), "exactly one character save");
            Assert.AreEqual(1, CountOf(body, "SaveBiotaToDatabase();"), "exactly one biota save");

            // both facet failures abandon before any rank is erased
            var strip = body.IndexOf("ClassAbilityRepricing.TryStripStoredAbilities(", StringComparison.Ordinal);
            var erase = body.IndexOf("Character.EraseQuest(", StringComparison.Ordinal);
            var read = body.IndexOf("TryGetCharacterFacetRows(out var facetRows)", StringComparison.Ordinal);
            Assert.AreEqual(2, CountOf(body.Substring(read, erase - read), "return false;"), "a failed facet read or write must return before touching ranks");
            Assert.IsTrue(strip < erase);
        }

        /// <summary>
        /// THE BIOTA SAVE WAITS FOR THE CHARACTER SAVE. The points, the stamp and the biota save live only in
        /// FinishClassAbilityRepricing; when ranks were erased, ApplyClassAbilityRepricing reaches it only from
        /// the character save's SUCCESS callback, marshalled onto the world thread. A failed save returns
        /// before that, leaving the character unstamped.
        /// </summary>
        [TestMethod]
        public void ApplyClassAbilityRepricing_PointsAndStampWaitForAConfirmedCharacterSave()
        {
            var body = ApplyBody();
            var finishAt = body.IndexOf("private void FinishClassAbilityRepricing(", StringComparison.Ordinal);
            var apply = body.Substring(0, finishAt);

            Assert.AreEqual(0, CountOf(apply, "SetProperty(PropertyInt.ClassAbilityRepriceWave"), "the stamp must not be set before the character save confirms");
            Assert.AreEqual(0, CountOf(apply, "SaveBiotaToDatabase"), "the biota save must not be queued before the character save confirms");
            Assert.AreEqual(0, CountOf(apply, "AdjustClassAbilityPoints("), "no point may move before the character save confirms");

            var save = apply.IndexOf("SaveCharacterToDatabase(saved =>", StringComparison.Ordinal);
            var callback = apply.Substring(save);

            var marshal = callback.IndexOf("WorldManager.EnqueueAction(new ActionEventDelegate(", StringComparison.Ordinal);
            var online = callback.IndexOf("PlayerManager.GetOnlinePlayer(Guid.Full) != this", StringComparison.Ordinal);
            var tryAt = callback.IndexOf("try\n", online, StringComparison.Ordinal);
            var failed = callback.IndexOf("if (!saved)", StringComparison.Ordinal);
            var failedReturn = callback.IndexOf("return;", failed, StringComparison.Ordinal);
            var stamped = callback.IndexOf("if ((GetProperty(PropertyInt.ClassAbilityRepriceWave) ?? 0) >= latestWave)", StringComparison.Ordinal);
            var finish = callback.IndexOf("FinishClassAbilityRepricing(context);", StringComparison.Ordinal);
            var finallyAt = callback.IndexOf("finally", StringComparison.Ordinal);
            var audit = callback.IndexOf("AuditClassAbilityPointLedger();", StringComparison.Ordinal);

            // everything, including the failed-save branch, is handled on the world thread
            Assert.IsTrue(marshal >= 0 && marshal < online, "the callback must marshal to the world thread before touching anything");
            Assert.IsTrue(online < tryAt && tryAt < failed, "an offline player is skipped before the try (nothing to audit)");
            Assert.IsTrue(failed < failedReturn && failedReturn < finish, "a failed save returns before the points phase");
            var skipSet = callback.IndexOf("skipAudit = true;", failed, StringComparison.Ordinal);
            Assert.IsTrue(skipSet > failed && skipSet < failedReturn, "the failed-save branch, and only it, flags the audit to be skipped");
            Assert.AreEqual(1, CountOf(callback, "skipAudit = true;"));
            Assert.IsTrue(callback.IndexOf("var skipAudit = false;", StringComparison.Ordinal) < tryAt, "the flag starts false before the try");
            Assert.IsTrue(stamped > failedReturn && stamped < finish, "an already-stamped character skips the points phase");
            Assert.IsTrue(finish < finallyAt && finallyAt < audit, "the deferred audit runs from a finally after the points phase");
            var gate = callback.IndexOf("if (!skipAudit)", finallyAt, StringComparison.Ordinal);
            Assert.IsTrue(gate > finallyAt && gate < audit, "the finally audits every online path except the failed save");

            // the synchronous path (nothing erased) finishes without waiting and does not defer the audit
            var syncFinish = apply.IndexOf("FinishClassAbilityRepricing(context);", StringComparison.Ordinal);
            Assert.IsTrue(syncFinish >= 0 && syncFinish < save, "nothing erased: finish synchronously");
            StringAssert.Contains(apply.Substring(syncFinish, save - syncFinish), "return false;");
            StringAssert.Contains(callback, "return true;");
        }

        [TestMethod]
        public void ApplyClassAbilityRepricing_LedgersUnconditionally_AndWarnsOnABindingFloor()
        {
            var body = ApplyBody();

            // THE NET ROW IS UNCONDITIONAL: it must not sit under a delta test
            var net = body.IndexOf("AdjustClassAbilityPoints(netDelta, 0, CapLedgerReason.Reprice", StringComparison.Ordinal);
            var preceding = body.Substring(0, net).TrimEnd();
            var lastLine = preceding.Substring(preceding.LastIndexOf('\n') + 1).Trim();
            Assert.IsFalse(lastLine.StartsWith("if", StringComparison.Ordinal), $"the net reprice row must be written even at delta 0, but it is guarded by: {lastLine}");
            StringAssert.Contains(body, "var netDelta = ClassAbilityRepricing.NetRowDelta(points, tokensAtOldPrice);");

            // one zero-delta row per unlearned ability at rank 0; one voucher_refund per token at its old price
            StringAssert.Contains(body, "AdjustClassAbilityPoints(0, 0, CapLedgerReason.Reprice,\n                        ability: def.Name, rankAfter: 0");
            StringAssert.Contains(body, "AdjustClassAbilityPoints(token.Decision.Total, 0, CapLedgerReason.VoucherRefund,");
            StringAssert.Contains(body, "AdjustClassAbilityPoints(0, 0, CapLedgerReason.Reprice,\n                        ability: promotion.Def.Name, rankAfter: promotion.Step.NewRank");
            Assert.AreEqual(3, CountOf(body, "CapLedgerReason.Reprice"), "the unlearn rows, the promotion rows and the net row");
            Assert.AreEqual(1, CountOf(body, "CapLedgerReason.VoucherRefund"), "the token rows");

            // the floor warning names the winning term and both values
            var warn = body.IndexOf("if (points.FloorBinds)", StringComparison.Ordinal);
            Assert.IsTrue(warn >= 0, "a binding floor must be logged");
            var warnLine = body.Substring(warn, body.IndexOf(';', warn) - warn);
            StringAssert.Contains(warnLine, "log.Warn(");
            StringAssert.Contains(warnLine, "FLOOR term won");
            StringAssert.Contains(warnLine, "{points.Recomputed}");
            StringAssert.Contains(warnLine, "{points.Floor}");
            StringAssert.Contains(warnLine, "{points.CurrentAvailable}");
            StringAssert.Contains(warnLine, "{points.OldPriceRefund}");
            StringAssert.Contains(warnLine, "{Name}");
            StringAssert.Contains(warnLine, "Guid.Full");

            // the floor's refund is measured from the ranks at login, before the erase
            var refund = body.IndexOf("ClassAbilityRepricing.OldPriceRefund(waves, appliedWave, RankBefore, tokensAtOldPrice, markerRefund)", StringComparison.Ordinal);
            var recompute = body.IndexOf("ClassAbilityRepricing.RecomputePoints(", StringComparison.Ordinal);
            Assert.IsTrue(refund >= 0 && refund < recompute, "the old-price refund must be computed from RankBefore and passed into the recompute");
            StringAssert.Contains(body, "ownedCostAfter, outstandingValue, expected)");
        }

        // ---- the production audit over real ledger rows ---------------------------------------------

        /// <summary>
        /// The CAP audit's own arithmetic (CapLedger.SinkSpend over ledger rows, then CapLedger.Unexplained
        /// over the counters) must read ZERO after the migration takes back a token. The ledger rows here are
        /// exactly the ones ApplyClassAbilityRepricing writes, with their deltas taken from the same pure
        /// helpers (DecideTokenRefund, RecomputePoints, NetRowDelta).
        /// </summary>
        [TestMethod]
        public void ProductionAudit_ReadsZero_AfterTheMigrationTakesBackATokenBoughtOnTheLedger()
        {
            // earned 20; owns Exsanguinate 1 (paid 3) and Blood Price 3 (paid 6); bought a Blood Price I token
            // (2, old price) and a Sanguine Ward I token (1) on the ledger. Available 20 - 9 - 3 = 8.
            var rows = new List<CharacterCapLedger>
            {
                LedgerRow(CapLedgerReason.GrantMilestone, 20, 20),
                LedgerRow(CapLedgerReason.Learn, -3, 0),
                LedgerRow(CapLedgerReason.Learn, -6, 0),
                LedgerRow(CapLedgerReason.VoucherBuy, -2, 0),
                LedgerRow(CapLedgerReason.VoucherBuy, -1, 0),
            };

            var available = 8;
            var ranks = new Dictionary<ClassAbilityId, int> { [ClassAbilityId.Exsanguinate] = 1, [ClassAbilityId.BloodPrice] = 3 };

            var bpToken = ClassAbilityRepricing.DecideTokenRefund(0, (int)ClassAbilityId.BloodPrice, 1, 1);
            var swToken = ClassAbilityRepricing.DecideTokenRefund(0, (int)ClassAbilityId.SanguineWard, 1, 1);
            Assert.IsTrue(bpToken.Refund);
            Assert.IsFalse(swToken.Refund, "Sanguine Ward I is unchanged in price and stays outstanding");

            // the migration
            var ownedAfter = ClassAbilityRegistry.Abilities[ClassAbilityId.BloodPrice].CumulativeCost(3);
            var outstanding = ClassAbilityRepricing.TokenValue((int)ClassAbilityId.SanguineWard, 1, 1);
            var expected = ClassAbilityRepricing.ExpectedDelta(W, 0, RankOf(ranks), bpToken.Total);
            var points = ClassAbilityRepricing.RecomputePoints(20, available, ownedAfter, outstanding, expected);

            rows.Add(LedgerRow(CapLedgerReason.Reprice, 0, 0));                                  // Exsanguinate unlearned
            rows.Add(LedgerRow(CapLedgerReason.VoucherRefund, bpToken.Total, 0));                // token taken back
            rows.Add(LedgerRow(CapLedgerReason.Reprice, ClassAbilityRepricing.NetRowDelta(points, bpToken.Total), 0));

            var availableAfter = available + rows.Skip(5).Sum(r => r.DeltaAvailable);
            Assert.AreEqual(points.NewAvailable, availableAfter, "the migration's rows sum to exactly the change in Available");
            Assert.AreEqual(16, availableAfter);

            var sinkSpend = CapLedger.SinkSpend(rows);
            Assert.AreEqual(1, sinkSpend, "only the Sanguine Ward I token is still sunk");
            Assert.AreEqual(0, CapLedger.Unexplained(20, availableAfter, ownedAfter, sinkSpend), "the production audit must read the migrated character as balanced");
        }

        [TestMethod]
        public void ProductionAudit_TheReviewersCase_TenEarnedThreeTokenTakenBack()
        {
            // earned 10, voucher_buy -3 for a Sanguine Ward III token (old price 3), nothing owned
            var token = ClassAbilityRepricing.DecideTokenRefund(0, (int)ClassAbilityId.SanguineWard, 3, 1);
            Assert.AreEqual(3, token.Total);

            var points = ClassAbilityRepricing.RecomputePoints(10, 7, 0, 0, ClassAbilityRepricing.ExpectedDelta(W, 0, _ => 0, token.Total));

            var rows = new List<CharacterCapLedger>
            {
                LedgerRow(CapLedgerReason.GrantMilestone, 10, 10),
                LedgerRow(CapLedgerReason.VoucherBuy, -3, 0),
                LedgerRow(CapLedgerReason.VoucherRefund, token.Total, 0),
                LedgerRow(CapLedgerReason.Reprice, ClassAbilityRepricing.NetRowDelta(points, token.Total), 0),
            };

            Assert.AreEqual(10, points.NewAvailable);
            Assert.AreEqual(0, CapLedger.SinkSpend(rows));
            Assert.AreEqual(0, CapLedger.Unexplained(10, points.NewAvailable, 0, CapLedger.SinkSpend(rows)));
        }

        [TestMethod]
        public void SinkSpend_NetsAnExchangerRefund_ThePreExistingGap()
        {
            var rows = new List<CharacterCapLedger>
            {
                LedgerRow(CapLedgerReason.VoucherBuy, -3, 0),
                LedgerRow(CapLedgerReason.VoucherRefund, 3, 0),
                LedgerRow(CapLedgerReason.VoucherBuy, -2, 0),
                LedgerRow(CapLedgerReason.VoucherRefundRetired, 2, 0),
            };

            Assert.AreEqual(0, CapLedger.SinkSpend(rows), "a refunded voucher is no longer sunk");
            Assert.AreEqual(0, CapLedger.Unexplained(totalEarned: 10, available: 10, ownedCost: 0, sinkSpend: CapLedger.SinkSpend(rows)));
        }

        // ---- the pending-refund marker ---------------------------------------------------------------

        /// <summary>
        /// THE BUG THE MARKER FIXES, replayed in pure math: the erase persisted (with the marker, same
        /// character save), the points phase never ran (logout before the callback), and the next login sees
        /// every pending rank at 0. Without the marker that character would own nothing, be untouched, and
        /// simply be stamped - the refund lost for good.
        /// </summary>
        [TestMethod]
        public void Marker_EraseThenSkipThenRerun_PaysTheOldPriceRefund()
        {
            // owned Exsanguinate 1 (paid 3) and Hemomancy 2 (paid 2); earned 5, nothing available
            var ranks = new Dictionary<ClassAbilityId, int> { [ClassAbilityId.Exsanguinate] = 1, [ClassAbilityId.Hemomancy] = 2 };

            // first login: the erase, and the marker written into the same character save
            var erased = ClassAbilityRepricing.PlanUnlearns(0, RankOf(ranks));
            var markerRefund = erased.Sum(s => s.OldPaid);
            var markerMask = ClassAbilityRepricing.EncodeUnlearnMask(W, erased.Select(s => s.Ability));
            Assert.AreEqual(5, markerRefund);

            // ... the callback is skipped (logged out). Next login: every pending rank reads 0
            Func<ClassAbilityId, int> after = _ => 0;

            Assert.AreEqual(0, ClassAbilityRepricing.PlanUnlearns(0, after).Count, "nothing left to erase");
            Assert.IsFalse(ClassAbilityRepricing.OwnsAnyPendingAbility(W, 0, after),
                "control: without the marker this character looks untouched and would only be stamped");

            var refund = ClassAbilityRepricing.OldPriceRefund(W, 0, after, 0, markerRefund);
            var points = ClassAbilityRepricing.RecomputePoints(totalEarned: 5, currentAvailable: 0, ownedCostAfter: 0, outstandingTokenValue: 0, oldPriceRefund: refund);

            Assert.AreEqual(5, refund);
            Assert.AreEqual(5, points.Delta, "Available rises by the old price of the erased ranks");

            // the chat line can still name them
            CollectionAssert.AreEqual(new[] { ClassAbilityId.Hemomancy, ClassAbilityId.Exsanguinate },
                ClassAbilityRepricing.DecodeUnlearnMask(W, markerMask).ToArray());

            // and the marker is what keeps the floor whole for an overcredited character (4 already held)
            var overcredited = ClassAbilityRepricing.RecomputePoints(5, 4, 0, 0, refund);
            Assert.AreEqual(9, overcredited.NewAvailable, "current 4 + the 5 the old prices owe");
            Assert.AreEqual(5, ClassAbilityRepricing.RecomputePoints(5, 4, 0, 0, ClassAbilityRepricing.OldPriceRefund(W, 0, after, 0, 0)).NewAvailable,
                "control: without the marker the floor would have lost the refund");
        }

        [TestMethod]
        public void Marker_InTheSameSession_IsNotCountedTwice()
        {
            var ranks = new Dictionary<ClassAbilityId, int> { [ClassAbilityId.Exsanguinate] = 1, [ClassAbilityId.SanguineWard] = 3 };
            var marker = ClassAbilityRepricing.PlanUnlearns(0, RankOf(ranks)).Sum(s => s.OldPaid);

            // the callback path still has the login ranks AND the marker this run wrote
            Assert.AreEqual(ClassAbilityRepricing.ExpectedDelta(W, 0, RankOf(ranks), 2),
                ClassAbilityRepricing.OldPriceRefund(W, 0, RankOf(ranks), 2, marker),
                "unlearned part from the marker only, kept part (Ward 6 -> 3) from the ranks, tokens once");
        }

        [TestMethod]
        public void Marker_StampPresent_IsStaleAndErasedUnpaid()
        {
            var body = ApplyBody();

            // the guard branch: stamp present -> clear the markers and return, before the kill switch,
            // the facet read, the erase or any point
            var guard = body.IndexOf("if (appliedWave >= latestWave)", StringComparison.Ordinal);
            var clear = body.IndexOf("ClearStaleRepriceMarkers(latestWave);", StringComparison.Ordinal);
            var guardReturn = body.IndexOf("return false;", clear, StringComparison.Ordinal);
            var killSwitch = body.IndexOf("\"class_ability_reprice_sweep_enabled\"", StringComparison.Ordinal);
            Assert.IsTrue(guard < clear && clear < guardReturn && guardReturn < killSwitch, "a stamped character clears stale markers and stops");

            // the points phase reads only markers ABOVE the applied wave, so a stamped character's are never read
            StringAssert.Contains(body, "for (var wave = appliedWave + 1; wave <= latestWave; wave++)");

            // and the marker is never erased in the session that pays it (only by a later stamped login)
            var finish = body.Substring(body.IndexOf("private void FinishClassAbilityRepricing(", StringComparison.Ordinal));
            Assert.IsFalse(finish.Contains("ClearStaleRepriceMarkers("), "the paying run must not erase the marker - a lost biota save would then lose the refund");
        }

        [TestMethod]
        public void Marker_KeysAreNotClassAbilityRanks_AndTheMaskRoundTrips()
        {
            foreach (var key in new[] { ClassAbilityRepricing.MarkerRefundKey(1), ClassAbilityRepricing.MarkerMaskKey(1) })
            {
                Assert.IsFalse(key.StartsWith(ClassAbilityRegistry.QuestKeyPrefix, StringComparison.OrdinalIgnoreCase), $"{key} must not read as a rank row");
                Assert.IsFalse(ClassAbilityRegistry.TryGetByQuestKey(key, out _));
            }

            var all = ClassAbilityRepricing.UnlearnBitOrder(W);
            CollectionAssert.AreEqual(Unlearned, all.ToArray(), "bit order is wave and row order");
            CollectionAssert.AreEqual(all.ToArray(), ClassAbilityRepricing.DecodeUnlearnMask(W, ClassAbilityRepricing.EncodeUnlearnMask(W, all)).ToArray());
            Assert.AreEqual(0, ClassAbilityRepricing.EncodeUnlearnMask(W, new[] { ClassAbilityId.SanguineWard }), "a kept ability has no bit");
        }

        // ---- wave 3: Berserker pass -----------------------------------------------------------------

        [TestMethod]
        public void Wave3_BerserkerPass_PinsItsRowsAndOldCosts()
        {
            var wave = W.Single(w => w.Number == 3);

            Assert.AreEqual("Berserker pass", wave.Label);
            Assert.AreEqual("Berserker abilities", wave.Subject);
            Assert.AreEqual("the Berserker trainer", wave.Trainer);

            var rows = wave.Rows.ToDictionary(r => r.Ability);
            CollectionAssert.AreEquivalent(new[] { ClassAbilityId.DamageRating, ClassAbilityId.Whirlwind }, rows.Keys.ToArray());

            // the CostPerRank values on master before this pass (8fd18f5d3)
            CollectionAssert.AreEqual(new[] { 2, 2, 2 }, rows[ClassAbilityId.DamageRating].OldCosts.ToArray());
            Assert.AreEqual(RepriceAction.Keep, rows[ClassAbilityId.DamageRating].Action);
            CollectionAssert.AreEqual(new[] { 3 }, rows[ClassAbilityId.Whirlwind].OldCosts.ToArray(), "Whirlwind was a single rank at 3");
            Assert.AreEqual(RepriceAction.PromoteToPaid, rows[ClassAbilityId.Whirlwind].Action);

            // the abilities that changed function at UNCHANGED prices deliberately have no row (review F4): an
            // unchanged-price Keep row moves no points and shows no notice, but forces a recompute and locks the
            // ability until the character migrates
            foreach (var id in new[] { ClassAbilityId.Frenzy, ClassAbilityId.Bloodlust, ClassAbilityId.Adrenaline, ClassAbilityId.BreakArmor })
                Assert.IsFalse(ClassAbilityRepricing.IsAbilityRepricePending(2, id), $"{id} is not locked behind wave 3");

            // the retired abilities are refunded by RetiredClassAbilities at login, never by a wave row
            foreach (var retired in new[] { ClassAbilityId.SavageBlows, ClassAbilityId.Vengeance })
                Assert.IsFalse(W.SelectMany(w => w.Rows).Any(r => r.Ability == retired), $"{retired} must not be in a wave");
        }

        [TestMethod]
        public void WhirlwindOwner_EndsAtRank3_WithNoNetPointChange()
        {
            foreach (var appliedWave in new[] { 0, 1, 2 })
            {
                var rankOf = RankOf(new Dictionary<ClassAbilityId, int> { [ClassAbilityId.Whirlwind] = 1 });

                var promotions = ClassAbilityRepricing.PlanPromotions(W, appliedWave, rankOf);

                Assert.AreEqual(1, promotions.Count, $"applied {appliedWave}: the owner is promoted");
                Assert.AreEqual(ClassAbilityId.Whirlwind, promotions[0].Ability);
                Assert.AreEqual(3, promotions[0].Wave);
                Assert.AreEqual(3, promotions[0].NewRank, "rank 1 bought at 3 becomes rank 3 at 1/1/1");

                Assert.AreEqual(3, ClassAbilityRegistry.Abilities[ClassAbilityId.Whirlwind].CumulativeCost(3), "control: rank 3 costs 3 live");
                Assert.AreEqual(0, ClassAbilityRepricing.OldPriceRefund(W, appliedWave, rankOf, 0, 0));
                Assert.AreEqual(0, ClassAbilityRepricing.RecomputePoints(20, 17, 3, 0, 0).Delta, "no net point change");
            }
        }

        [TestMethod]
        public void DamageRatingOwner_KeepsTheRank_AndGetsTheDifferenceBack()
        {
            // a rank-3 owner paid 6 at 2/2/2; rank 3 costs 3 at 1/1/1, so 3 comes back and the rank stays
            var rankOf = RankOf(new Dictionary<ClassAbilityId, int> { [ClassAbilityId.DamageRating] = 3 });

            Assert.AreEqual(0, ClassAbilityRepricing.PlanUnlearns(W, 2, rankOf).Count, "Keep never unlearns");
            Assert.AreEqual(3, ClassAbilityRepricing.ExpectedDelta(W, 2, rankOf, 0));

            var expected = ClassAbilityRepricing.OldPriceRefund(W, 2, rankOf, 0, 0);
            var points = ClassAbilityRepricing.RecomputePoints(20, 14, 3, 0, expected);
            Assert.AreEqual(3, points.Delta);
            Assert.IsFalse(points.FloorBinds);

            // a carried old Damage Rating token (bought at 2) is taken back at its old price
            var token = ClassAbilityRepricing.DecideTokenRefund(W, 2, (int)ClassAbilityId.DamageRating, 1, 1);
            Assert.IsTrue(token.Refund);
            Assert.AreEqual(2, token.UnitPrice);

            // and a carried old Whirlwind token (tier 1, bought at 3) likewise
            var ww = ClassAbilityRepricing.DecideTokenRefund(W, 2, (int)ClassAbilityId.Whirlwind, 1, 1);
            Assert.IsTrue(ww.Refund);
            Assert.AreEqual(3, ww.UnitPrice);

            // a Frenzy token kept its price, so it stays in the pack
            Assert.IsFalse(ClassAbilityRepricing.DecideTokenRefund(W, 2, (int)ClassAbilityId.Frenzy, 1, 1).Refund);
        }

        // ---- wave 6: Archmage pass ----------------------------------------------------------------

        [TestMethod]
        public void Wave6_ArchmagePass_PinsItsRowsAndOldCosts()
        {
            var wave = W.Single(w => w.Number == 6);

            Assert.AreEqual("Archmage pass", wave.Label);
            Assert.AreEqual("Archmage abilities", wave.Subject);
            Assert.AreEqual("the Archmage trainer", wave.Trainer);

            var rows = wave.Rows.ToDictionary(r => r.Ability);
            CollectionAssert.AreEquivalent(new[] { ClassAbilityId.Overchannel, ClassAbilityId.EchoCast, ClassAbilityId.ManaBarrier },
                rows.Keys.ToArray(), "only the three repriced abilities - no Keep rows for unchanged prices");

            CollectionAssert.AreEqual(new[] { 2, 2, 2 }, rows[ClassAbilityId.Overchannel].OldCosts.ToArray());
            CollectionAssert.AreEqual(new[] { 2, 2, 2 }, rows[ClassAbilityId.EchoCast].OldCosts.ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, rows[ClassAbilityId.ManaBarrier].OldCosts.ToArray());

            foreach (var row in rows.Values)
            {
                Assert.AreEqual(RepriceAction.Keep, row.Action, $"{row.Ability} keeps its ranks");
                CollectionAssert.AreEqual(new[] { 1, 1, 1 }, row.NewCosts.ToArray(), $"{row.Ability}: 1/1/1");
            }

            // the retired abilities are refunded by RetiredClassAbilities at login, never by a wave row
            foreach (var retired in new[] { ClassAbilityId.QuickenedCasting, ClassAbilityId.FlatCastSpeed })
                Assert.IsFalse(W.SelectMany(w => w.Rows).Any(r => r.Ability == retired), $"{retired} must not be in a wave");
        }

        [TestMethod]
        public void ArchmageOwners_KeepTheirRanks_AndGetTheDifferenceBack()
        {
            // rank 3 Overchannel (paid 6), rank 3 Echo Cast (paid 6), rank 3 Mana Barrier (paid 1+2+3 = 6):
            // each now costs 3, so 9 comes back and every rank stays
            var rankOf = RankOf(new Dictionary<ClassAbilityId, int>
            {
                [ClassAbilityId.Overchannel] = 3,
                [ClassAbilityId.EchoCast] = 3,
                [ClassAbilityId.ManaBarrier] = 3,
            });

            Assert.AreEqual(0, ClassAbilityRepricing.PlanUnlearns(W, 3, rankOf).Count, "Keep never unlearns");
            Assert.AreEqual(9, ClassAbilityRepricing.ExpectedDelta(W, 3, rankOf, 0));

            // a rank 2 Mana Barrier owner paid 1+2 = 3 and now owes 2, so 1 comes back
            var mb2 = RankOf(new Dictionary<ClassAbilityId, int> { [ClassAbilityId.ManaBarrier] = 2 });
            Assert.AreEqual(1, ClassAbilityRepricing.ExpectedDelta(W, 3, mb2, 0));

            // a carried old Mana Barrier rank-3 token (bought at 3) is taken back at its old price
            var token = ClassAbilityRepricing.DecideTokenRefund(W, 3, (int)ClassAbilityId.ManaBarrier, 3, 1);
            Assert.IsTrue(token.Refund);
            Assert.AreEqual(3, token.UnitPrice);

            // a Mana Barrier rank-1 token kept its price (1 -> 1), so it stays in the pack
            Assert.IsFalse(ClassAbilityRepricing.DecideTokenRefund(W, 3, (int)ClassAbilityId.ManaBarrier, 1, 1).Refund);

            // a character already stamped at wave 6 owes nothing
            Assert.AreEqual(0, ClassAbilityRepricing.ExpectedDelta(W, 6, rankOf, 0));
        }

        // ---- wave 2: Taunt, PromoteToPaid ----------------------------------------------------------

        [TestMethod]
        public void Wave2_PromotesTaunt_AndPinsItsOldCost()
        {
            var wave = W.Single(w => w.Number == 2);
            var row = wave.Rows.Single();

            Assert.AreEqual(ClassAbilityId.Taunt, row.Ability);
            Assert.AreEqual(RepriceAction.PromoteToPaid, row.Action);
            CollectionAssert.AreEqual(new[] { 3 }, row.OldCosts.ToArray(), "Taunt was a single rank at 3");
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, row.NewCosts.ToArray());
            Assert.AreEqual("Vanguard abilities", wave.Subject);
            Assert.AreEqual("the Vanguard trainer", wave.Trainer);

            // the merged Blood Mage wave is untouched by the new one
            Assert.IsFalse(W.Single(w => w.Number == 1).Rows.Any(r => r.Ability == ClassAbilityId.Taunt));

            Assert.IsTrue(ClassAbilityRepricing.IsAbilityRepricePending(1, ClassAbilityId.Taunt), "a character past wave 1 still owes wave 2");
            Assert.IsFalse(ClassAbilityRepricing.IsAbilityRepricePending(2, ClassAbilityId.Taunt));
        }

        [TestMethod]
        public void PromotedRank_BuysTheNewLadderWithWhatWasPaid_NeverLowers()
        {
            var old3 = new[] { 3 };
            var new111 = new[] { 1, 1, 1 };

            Assert.AreEqual(3, ClassAbilityRepricing.PromotedRank(old3, new111, 1), "paid 3 buys all three new ranks");
            Assert.AreEqual(0, ClassAbilityRepricing.PromotedRank(old3, new111, 0), "an unowned ability is not granted");
            Assert.AreEqual(3, ClassAbilityRepricing.PromotedRank(old3, new111, 3), "a re-run over a persisted promotion promotes to itself");

            // a price RISE never takes a rank away: rank 3 at 1/1/1 paid 3, which buys only one rank at 2/2/2
            Assert.AreEqual(3, ClassAbilityRepricing.PromotedRank(new111, new[] { 2, 2, 2 }, 3));

            // a partial buy: paid 3 against 2/2/2 reaches rank 1 only, so a rank-1 owner stays at rank 1
            Assert.AreEqual(1, ClassAbilityRepricing.PromotedRank(old3, new[] { 2, 2, 2 }, 1));
        }

        [TestMethod]
        public void TauntOwner_EndsAtRank3_WithNoNetPointChange()
        {
            foreach (var appliedWave in new[] { 0, 1 })   // both waves pending, or only the Vanguard one
            {
                var ranks = new Dictionary<ClassAbilityId, int> { [ClassAbilityId.Taunt] = 1 };
                var rankOf = RankOf(ranks);

                var promotions = ClassAbilityRepricing.PlanPromotions(W, appliedWave, rankOf);

                Assert.AreEqual(1, promotions.Count, $"applied {appliedWave}: the owner is promoted");
                Assert.AreEqual(ClassAbilityId.Taunt, promotions[0].Ability);
                Assert.AreEqual(2, promotions[0].Wave);
                Assert.AreEqual(1, promotions[0].OldRank);
                Assert.AreEqual(3, promotions[0].NewRank, "rank 1 bought at 3 becomes rank 3 at 1/1/1");
                Assert.AreEqual(3, promotions[0].OldPaid);

                // consistent before: earned 20 = available 17 + the 3 paid for Taunt rank 1
                const int available = 17;
                var earned = available + 3;

                var ownedAfter = ClassAbilityRegistry.Abilities[ClassAbilityId.Taunt].CumulativeCost(3);
                Assert.AreEqual(3, ownedAfter, "control: rank 3 costs 3 at the live ladder");

                var expected = ClassAbilityRepricing.OldPriceRefund(W, appliedWave, rankOf, 0, 0);
                Assert.AreEqual(0, expected, "the old-price refund for a promotion to exactly what was paid is 0");

                var points = ClassAbilityRepricing.RecomputePoints(earned, available, ownedAfter, 0, expected);

                Assert.AreEqual(available, points.NewAvailable, "no net point change");
                Assert.AreEqual(0, points.Delta);
                Assert.IsFalse(points.FloorBinds, "the recompute and the floor agree for a consistent balance");
            }
        }

        [TestMethod]
        public void TauntPromotion_ReRunAfterAPersistedPromotion_PlansNothing_AndMovesNoPoints()
        {
            var rankOf = RankOf(new Dictionary<ClassAbilityId, int> { [ClassAbilityId.Taunt] = 3 });

            Assert.AreEqual(0, ClassAbilityRepricing.PlanPromotions(W, 1, rankOf).Count, "already at rank 3");
            Assert.AreEqual(0, ClassAbilityRepricing.OldPriceRefund(W, 1, rankOf, 0, 0), "and the floor adds nothing");

            var points = ClassAbilityRepricing.RecomputePoints(20, 17, 3, 0, 0);
            Assert.AreEqual(0, points.Delta);
        }

        [TestMethod]
        public void TauntPromotion_ControlKeepWouldHaveRefundedAndLeftRank1()
        {
            // the reason a third action exists: the same row as Keep leaves rank 1 and refunds 2
            var keepWaves = new[] { new RepriceWave(1, "keep", "s", "t", new RepriceRow(ClassAbilityId.Taunt, new[] { 3 }, new[] { 1, 1, 1 }, RepriceAction.Keep)) };
            var rankOf = RankOf(new Dictionary<ClassAbilityId, int> { [ClassAbilityId.Taunt] = 1 });

            Assert.AreEqual(0, ClassAbilityRepricing.PlanPromotions(keepWaves, 0, rankOf).Count);
            Assert.AreEqual(2, ClassAbilityRepricing.ExpectedDelta(keepWaves, 0, rankOf, 0), "Keep prices rank 1 at the new ladder: 3 - 1");
        }

        [TestMethod]
        public void PlanPromotions_AnUnlearnInAnyPendingWaveWins()
        {
            var waves = new[]
            {
                new RepriceWave(1, "promote", "s", "t", new RepriceRow(ClassAbilityId.Taunt, new[] { 3 }, new[] { 1, 1, 1 }, RepriceAction.PromoteToPaid)),
                new RepriceWave(2, "rework", "s", "t", new RepriceRow(ClassAbilityId.Taunt, new[] { 1, 1, 1 }, new[] { 1, 1, 1 }, RepriceAction.Unlearn)),
            };

            var rankOf = RankOf(new Dictionary<ClassAbilityId, int> { [ClassAbilityId.Taunt] = 1 });

            Assert.AreEqual(0, ClassAbilityRepricing.PlanPromotions(waves, 0, rankOf).Count, "an ability a pending wave unlearns is never promoted");
            Assert.AreEqual(1, ClassAbilityRepricing.PlanUnlearns(waves, 0, rankOf).Count);

            // control: with only the promote wave pending, it IS promoted
            Assert.AreEqual(1, ClassAbilityRepricing.PlanPromotions(waves.Take(1).ToArray(), 0, rankOf).Count);
        }

        /// <summary>
        /// PREPAID TAUNT TOKENS KEEP THEIR VALUE. An unused old Taunt token is tier 1 bought at 3. The new
        /// tier-1 price is 1, so the decision takes it back at its OLD price, and the recompute returns the
        /// full 3 (the token leaves the outstanding value, which the exchanger would only have valued at 1).
        /// Tier 2 and 3 tokens did not exist at the old prices and are left alone.
        /// </summary>
        [TestMethod]
        public void PrepaidTauntTokens_AreTakenBackAtTheirOldPrice_AndReturnedInFull()
        {
            foreach (var appliedWave in new[] { 0, 1 })
            {
                var one = ClassAbilityRepricing.DecideTokenRefund(W, appliedWave, (int)ClassAbilityId.Taunt, 1, 1);
                Assert.IsTrue(one.Refund, $"applied {appliedWave}: the old token is taken back");
                Assert.IsFalse(one.Unlearned, "taken back because its price changed, not because of an unlearn");
                Assert.AreEqual(3, one.UnitPrice, "bought at 3");

                var stack = ClassAbilityRepricing.DecideTokenRefund(W, appliedWave, (int)ClassAbilityId.Taunt, 1, 2);
                Assert.AreEqual(6, stack.Total, "a stack of two returns 6");

                Assert.IsFalse(ClassAbilityRepricing.DecideTokenRefund(W, appliedWave, (int)ClassAbilityId.Taunt, 2, 1).Refund, "tier 2 did not exist at the old prices");
                Assert.IsFalse(ClassAbilityRepricing.DecideTokenRefund(W, appliedWave, (int)ClassAbilityId.Taunt, 3, 1).Refund, "tier 3 did not exist at the old prices");
            }

            Assert.AreEqual(1, ClassAbilityRepricing.TokenValue((int)ClassAbilityId.Taunt, 1, 1),
                "control: left in the pack, the exchanger would value it at the NEW price, which is why it is taken back");

            // a character with no Taunt rank and one carried old token: earned 13 = available 10 + 3 in the token
            var rankOf = RankOf(new Dictionary<ClassAbilityId, int>());
            var expected = ClassAbilityRepricing.OldPriceRefund(W, 1, rankOf, 3, 0);
            var points = ClassAbilityRepricing.RecomputePoints(13, 10, 0, 0, expected);

            Assert.AreEqual(3, points.Delta, "the token's full prepaid 3 points come back");
            Assert.IsFalse(points.FloorBinds);

            // a Taunt owner carrying one old token too: promoted at no change, plus the token's 3
            var owner = RankOf(new Dictionary<ClassAbilityId, int> { [ClassAbilityId.Taunt] = 1 });
            var ownerExpected = ClassAbilityRepricing.OldPriceRefund(W, 1, owner, 3, 0);
            var ownerPoints = ClassAbilityRepricing.RecomputePoints(10 + 3 + 3, 10, 3, 0, ownerExpected);

            Assert.AreEqual(3, ownerPoints.Delta);
            Assert.IsFalse(ownerPoints.FloorBinds);
        }

        [TestMethod]
        public void NoticeWave_NamesTheWaveThatTouchedTheCharacter()
        {
            Assert.AreEqual(1, ClassAbilityRepricing.NoticeWave(W, 0, new[] { ClassAbilityId.Hemomancy }).Number,
                "a character that missed wave 1 is told about the Blood Mage abilities, not the Vanguard ones");
            Assert.AreEqual(2, ClassAbilityRepricing.NoticeWave(W, 0, new[] { ClassAbilityId.Taunt }).Number);
            Assert.AreEqual(2, ClassAbilityRepricing.NoticeWave(W, 1, new[] { ClassAbilityId.Taunt }).Number);
            Assert.AreEqual(3, ClassAbilityRepricing.NoticeWave(W, 0, new[] { ClassAbilityId.Whirlwind }).Number, "the Berserker pass names its own wave");
            Assert.AreEqual(4, ClassAbilityRepricing.NoticeWave(W, 2, new[] { ClassAbilityId.Spellsurge }).Number, "the Spellsword pass names its own wave even with wave 3 also pending");
            Assert.AreEqual(5, ClassAbilityRepricing.NoticeWave(W, 0, new[] { ClassAbilityId.CritRating }).Number, "the Archer pass names its own wave");
            Assert.AreEqual(ClassAbilityRepricing.LatestWave, ClassAbilityRepricing.NoticeWave(W, 0, new ClassAbilityId[0]).Number, "nothing touched falls back to the last pending wave");
            Assert.IsNull(ClassAbilityRepricing.NoticeWave(W, ClassAbilityRepricing.LatestWave, new[] { ClassAbilityId.Taunt }));
        }

        [TestMethod]
        public void NoticeWording_OneContributingWave_KeepsItsOwnWording()
        {
            var bloodOnly = ClassAbilityRepricing.NoticeWording(W, 0, new[] { ClassAbilityId.Hemomancy }, new[] { ClassAbilityId.Hemomancy });
            Assert.AreEqual(("Blood Mage abilities", "the Blood Mage trainer"), bloodOnly);

            var tauntOnly = ClassAbilityRepricing.NoticeWording(W, 0, new[] { ClassAbilityId.Taunt }, new ClassAbilityId[0]);
            Assert.AreEqual(("Vanguard abilities", "the Vanguard trainer"), tauntOnly);
        }

        [TestMethod]
        public void NoticeWording_TwoContributingWaves_IsNeutral_AndKeepsTheOnlyUnlearnTrainer()
        {
            var both = ClassAbilityRepricing.NoticeWording(W, 0,
                new[] { ClassAbilityId.Hemomancy, ClassAbilityId.Taunt }, new[] { ClassAbilityId.Hemomancy });

            Assert.AreEqual(ClassAbilityRepricing.MultiWaveSubject, both.Subject, "the total spans two waves, so no one wave is named");
            Assert.AreEqual("the Blood Mage trainer", both.Trainer, "the only wave that unlearned anything is where the player re-learns");

            var noUnlearn = ClassAbilityRepricing.NoticeWording(W, 0,
                new[] { ClassAbilityId.SanguineReserve, ClassAbilityId.Taunt }, new ClassAbilityId[0]);
            Assert.AreEqual(ClassAbilityRepricing.MultiWaveSubject, noUnlearn.Subject);
            Assert.AreEqual(ClassAbilityRepricing.MultiWaveTrainer, noUnlearn.Trainer);
        }

        /// <summary>
        /// The release-day case: neither wave applied, the character owns a Keep ability whose price did not
        /// change (Sanguine Reserve rank 1, 1 -> 1) and carries one old tier-1 Taunt token (bought at 3). Two
        /// waves contribute, the 3 points come back from the Vanguard one, and the line must not say the Blood
        /// Mage abilities returned them.
        /// </summary>
        [TestMethod]
        public void BothWavesPending_UnchangedKeepAbility_PlusOldTauntToken_IsWordedNeutrally()
        {
            var rankOf = RankOf(new Dictionary<ClassAbilityId, int> { [ClassAbilityId.SanguineReserve] = 1 });

            var token = ClassAbilityRepricing.DecideTokenRefund(W, 0, (int)ClassAbilityId.Taunt, 1, 1);
            Assert.IsTrue(token.Refund);
            Assert.AreEqual(3, token.Total);

            Assert.AreEqual(0, ClassAbilityRepricing.PlanUnlearns(W, 0, rankOf).Count);
            Assert.AreEqual(0, ClassAbilityRepricing.PlanPromotions(W, 0, rankOf).Count);

            // consistent before: earned 14 = available 10 + 1 (Sanguine Reserve rank 1) + 3 (the token)
            var expected = ClassAbilityRepricing.OldPriceRefund(W, 0, rankOf, token.Total, 0);
            Assert.AreEqual(3, expected, "the kept rank's price did not change, so only the token's 3");

            var points = ClassAbilityRepricing.RecomputePoints(14, 10, 1, 0, expected);
            Assert.AreEqual(3, points.Delta);
            Assert.IsFalse(points.FloorBinds);

            var wording = ClassAbilityRepricing.NoticeWording(W, 0,
                new[] { ClassAbilityId.SanguineReserve, ClassAbilityId.Taunt }, new ClassAbilityId[0]);

            var lines = ClassAbilityRepricing.BuildNoticeLines(wording.Subject, wording.Trainer, new string[0], points.Delta, 1, null);

            Assert.AreEqual(2, lines.Count);
            Assert.AreEqual("Some class abilities were reworked and repriced: 3 class ability points returned.", lines[0]);
            Assert.AreEqual("One unused training token was taken back; its points are included above.", lines[1]);

            // control: the single-wave picker alone would have credited the Blood Mage wave with the Taunt token's points
            Assert.AreEqual(1, ClassAbilityRepricing.NoticeWave(W, 0, new[] { ClassAbilityId.SanguineReserve, ClassAbilityId.Taunt }).Number);
        }

        [TestMethod]
        public void WaveNaming_LabelsAnErasedAbilityByTheWaveThatUnlearnsIt()
        {
            Assert.AreEqual(1, ClassAbilityRepricing.WaveNaming(W, 0, ClassAbilityId.Hemomancy).Number,
                "with both waves pending, Hemomancy's erase belongs to the Blood Mage wave, not the last one pending");
            Assert.AreEqual(2, ClassAbilityRepricing.WaveNaming(W, 0, ClassAbilityId.Taunt).Number);
            Assert.IsNull(ClassAbilityRepricing.WaveNaming(W, 1, ClassAbilityId.Hemomancy), "no pending wave names it once wave 1 is applied");

            // the Player-bound half uses it for the interrupted-login row
            StringAssert.Contains(ApplyBody(), "ClassAbilityRepricing.WaveNaming(waves, appliedWave, id)?.Label");
            StringAssert.Contains(ApplyBody(), "ClassAbilityRepricing.NoticeWording(waves, appliedWave, touchedIds, erasedIds)");
        }

        [TestMethod]
        public void NoticeLines_APromotionGetsItsOwnLine()
        {
            var promoted = new List<(string, int)> { ("Taunt", 3) };

            var only = ClassAbilityRepricing.BuildNoticeLines("Vanguard abilities", "the Vanguard trainer", new string[0], 0, 0, null, promoted);
            Assert.AreEqual("Taunt now has more ranks: you were raised to rank 3 at no cost.", only.Single());

            var withToken = ClassAbilityRepricing.BuildNoticeLines("Vanguard abilities", "the Vanguard trainer", new string[0], 3, 1, null, promoted);
            Assert.AreEqual(3, withToken.Count);
            Assert.AreEqual("Vanguard abilities were reworked and repriced: 3 class ability points returned.", withToken[0]);
            Assert.AreEqual("Taunt now has more ranks: you were raised to rank 3 at no cost.", withToken[2]);

            foreach (var line in withToken)
                Assert.IsTrue(line.Length <= 250, $"chat line too long ({line.Length}): {line}");
        }

        // ---- wave 4: the Spellsword pass, the first MIXED wave (Keep + PromoteToPaid) ------------------

        [TestMethod]
        public void Wave4_PinsTheSpellswordOldCosts_AndMixesKeepWithPromoteToPaid()
        {
            var wave = W.Single(w => w.Number == 4);

            Assert.AreEqual("Spellsword abilities", wave.Subject);
            Assert.AreEqual("the Spellsword trainer", wave.Trainer);
            Assert.AreEqual(5, wave.Rows.Count);

            void Pin(ClassAbilityId id, int[] oldCosts, RepriceAction action)
            {
                var row = wave.Rows.Single(r => r.Ability == id);
                CollectionAssert.AreEqual(oldCosts, row.OldCosts.ToArray(), $"{id}: old costs (master 8fd18f5d3)");
                CollectionAssert.AreEqual(new[] { 1, 1, 1 }, row.NewCosts.ToArray(), $"{id}: new costs");
                Assert.AreEqual(action, row.Action, $"{id}: action");
            }

            Pin(ClassAbilityId.Sundermark,     new[] { 2, 2, 2 }, RepriceAction.Keep);
            Pin(ClassAbilityId.DispellingEdge, new[] { 2, 2, 2 }, RepriceAction.Keep);
            Pin(ClassAbilityId.Spellsurge,     new[] { 3 },       RepriceAction.PromoteToPaid);
            Pin(ClassAbilityId.Spellstorm,     new[] { 3 },       RepriceAction.PromoteToPaid);
            Pin(ClassAbilityId.Cascade,        new[] { 3 },       RepriceAction.PromoteToPaid);

            Assert.IsFalse(W.Any(w => w.Number == 3 && w.Rows.Any(r => wave.Rows.Any(x => x.Ability == r.Ability))),
                "wave 3 is reserved for the Berserker pass and never names a Spellsword ability");

            // a character past wave 2 (or past a landed wave 3) still owes wave 4
            foreach (var applied in new[] { 0, 2, 3 })
                Assert.IsTrue(ClassAbilityRepricing.IsAbilityRepricePending(applied, ClassAbilityId.Sundermark), $"applied {applied}");
            Assert.IsFalse(ClassAbilityRepricing.IsAbilityRepricePending(4, ClassAbilityId.Cascade));
        }

        /// <summary>
        /// A full Spellsword owner through wave 4: the three single-rank abilities (paid 3 each) are promoted
        /// to rank 3 at no change, while Sundermark rank 3 (paid 6, now 3) and Dispelling Dance rank 2 (paid
        /// 4, now 2) keep their ranks and return the difference, 3 + 2 = 5.
        /// </summary>
        [TestMethod]
        public void Wave4_MixedOwner_PromotesTheThreeLadders_AndRefundsTheTwoKeepPriceDrops()
        {
            foreach (var appliedWave in new[] { 0, 2 })
            {
                var ranks = new Dictionary<ClassAbilityId, int>
                {
                    [ClassAbilityId.Spellsurge] = 1, [ClassAbilityId.Spellstorm] = 1, [ClassAbilityId.Cascade] = 1,
                    [ClassAbilityId.Sundermark] = 3, [ClassAbilityId.DispellingEdge] = 2,
                };
                var rankOf = RankOf(ranks);

                Assert.AreEqual(0, ClassAbilityRepricing.PlanUnlearns(W, appliedWave, rankOf).Count, "wave 4 unlearns nothing");

                var promotions = ClassAbilityRepricing.PlanPromotions(W, appliedWave, rankOf);
                CollectionAssert.AreEquivalent(new[] { ClassAbilityId.Spellsurge, ClassAbilityId.Spellstorm, ClassAbilityId.Cascade },
                    promotions.Select(p => p.Ability).ToArray(), $"applied {appliedWave}: only the PromoteToPaid rows promote");

                foreach (var p in promotions)
                {
                    Assert.AreEqual(4, p.Wave);
                    Assert.AreEqual(1, p.OldRank);
                    Assert.AreEqual(3, p.NewRank, $"{p.Ability}: paid 3 buys all three 1/1/1 ranks");
                    Assert.AreEqual(3, p.OldPaid);
                }

                // consistent before: earned = available 10 + paid 3 + 3 + 3 + 6 + 4
                const int available = 10;
                var earned = available + 19;

                var ownedAfter = 3 * 3
                    + ClassAbilityRegistry.Abilities[ClassAbilityId.Sundermark].CumulativeCost(3)
                    + ClassAbilityRegistry.Abilities[ClassAbilityId.DispellingEdge].CumulativeCost(2);
                Assert.AreEqual(14, ownedAfter, "control: 3 x rank 3 at 1/1/1, Sundermark 3, Dispelling Dance 2");

                var expected = ClassAbilityRepricing.OldPriceRefund(W, appliedWave, rankOf, 0, 0);
                Assert.AreEqual(5, expected, "the Keep price drops only: Sundermark 6 -> 3, Dispelling Dance 4 -> 2");

                var points = ClassAbilityRepricing.RecomputePoints(earned, available, ownedAfter, 0, expected);
                Assert.AreEqual(15, points.NewAvailable);
                Assert.AreEqual(5, points.Delta);
                Assert.IsFalse(points.FloorBinds, "the recompute and the floor agree for a consistent balance");
            }
        }

        [TestMethod]
        public void Wave4_CarriedTokens_TakenBackOnlyWhereTheTierPriceChanged()
        {
            var sm2 = ClassAbilityRepricing.DecideTokenRefund(W, 2, (int)ClassAbilityId.Sundermark, 2, 1);
            Assert.IsTrue(sm2.Refund);
            Assert.IsFalse(sm2.Unlearned, "a Keep row's token is taken back for its price, not an unlearn");
            Assert.AreEqual(2, sm2.UnitPrice, "bought at 2");

            Assert.AreEqual(2, ClassAbilityRepricing.DecideTokenRefund(W, 2, (int)ClassAbilityId.DispellingEdge, 3, 1).UnitPrice);

            var surge1 = ClassAbilityRepricing.DecideTokenRefund(W, 2, (int)ClassAbilityId.Spellsurge, 1, 2);
            Assert.IsTrue(surge1.Refund);
            Assert.AreEqual(3, surge1.UnitPrice, "the old single-rank token was bought at 3");
            Assert.AreEqual(6, surge1.Total);

            foreach (var id in new[] { ClassAbilityId.Spellsurge, ClassAbilityId.Spellstorm, ClassAbilityId.Cascade })
                for (var tier = 2; tier <= 3; tier++)
                    Assert.IsFalse(ClassAbilityRepricing.DecideTokenRefund(W, 2, (int)id, tier, 1).Refund, $"{id} tier {tier} did not exist at the old prices");

            Assert.IsFalse(ClassAbilityRepricing.DecideTokenRefund(W, 4, (int)ClassAbilityId.Sundermark, 2, 1).Refund, "control: stamped at 4");
        }

        [TestMethod]
        public void ApplyClassAbilityRepricing_PromotesBeforeTheSave_AndWaitsForItToConfirm()
        {
            var body = ApplyBody();

            var plan = body.IndexOf("ClassAbilityRepricing.PlanPromotions(waves, appliedWave, RankBefore)", StringComparison.Ordinal);
            var gate = body.IndexOf("if (unlearned.Count == 0 && promoted.Count == 0)", StringComparison.Ordinal);
            var save = body.IndexOf("SaveCharacterToDatabase(saved =>", StringComparison.Ordinal);

            Assert.IsTrue(plan >= 0, "the promotions must be planned from the ranks at login");
            Assert.IsTrue(gate > plan, "a promotion must be written before the synchronous-finish gate");
            Assert.IsTrue(save > gate, "and a run that promoted must wait for the confirmed character save, like one that erased");

            StringAssert.Contains(body, "quest.NumTimesCompleted = step.NewRank;");
        }

        // ---- helpers -------------------------------------------------------------------------------

        private sealed class Outcome
        {
            public int Total;
            public int OwnedAfter;
            public int OutstandingValue;
            public int Expected;
            public RepricePoints Points;
            public Dictionary<ClassAbilityId, int> RanksAfter;
            public List<(int TokenId, int Tier, int Stack)> Outstanding;
        }

        /// <summary>
        /// A pure model of one character through ApplyClassAbilityRepricing's points path. Ranks of wave
        /// abilities and tokens for them were PAID at the wave's old costs; anything else at its live cost.
        /// The balance before is consistent (earned = available + paid ranks + paid tokens), plus an optional
        /// pre-existing overcredit.
        /// </summary>
        private static Outcome Simulate(Dictionary<ClassAbilityId, int> ranks, List<(int TokenId, int Tier, int Stack)> tokens,
            int availableBefore, int extraOwnedPaid, int overcredit = 0)
        {
            var waveRows = W.Single(w => w.Number == 1).Rows.ToDictionary(r => r.Ability);

            int Paid(ClassAbilityId id, int rank) => waveRows.TryGetValue(id, out var row)
                ? ClassAbilityRepricing.Cumulative(row.OldCosts, rank)
                : ClassAbilityRegistry.Abilities[id].CumulativeCost(rank);

            int TokenPaid((int TokenId, int Tier, int Stack) t) => waveRows.TryGetValue((ClassAbilityId)t.TokenId, out var row)
                ? row.OldCosts[t.Tier - 1] * Math.Max(1, t.Stack)
                : ClassAbilityRepricing.TokenValue(t.TokenId, t.Tier, t.Stack);

            var paidRanks = ranks.Where(kv => kv.Value > 0).Sum(kv => Paid(kv.Key, kv.Value)) + extraOwnedPaid;
            var paidTokens = tokens.Sum(TokenPaid);
            var total = availableBefore + paidRanks + paidTokens;
            var current = availableBefore + overcredit;

            var rankOf = RankOf(ranks);

            var unlearned = new HashSet<ClassAbilityId>(ClassAbilityRepricing.PlanUnlearns(0, rankOf).Select(s => s.Ability));
            var ranksAfter = ranks.Where(kv => kv.Value > 0 && !unlearned.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

            var ownedAfter = ranksAfter.Sum(kv => ClassAbilityRegistry.Abilities[kv.Key].CumulativeCost(kv.Value)) + extraOwnedPaid;

            var outstanding = new List<(int TokenId, int Tier, int Stack)>();
            var takenAtOld = 0;

            foreach (var t in tokens)
            {
                var d = ClassAbilityRepricing.DecideTokenRefund(0, t.TokenId, t.Tier, t.Stack);
                if (d.Refund)
                    takenAtOld += d.Total;
                else
                    outstanding.Add(t);
            }

            var outstandingValue = ClassAbilityRepricing.OutstandingTokenValue(outstanding);
            var expected = ClassAbilityRepricing.ExpectedDelta(W, 0, rankOf, takenAtOld);

            return new Outcome
            {
                Total = total,
                OwnedAfter = ownedAfter,
                OutstandingValue = outstandingValue,
                Points = ClassAbilityRepricing.RecomputePoints(total, current, ownedAfter, outstandingValue, expected),
                Expected = expected,
                RanksAfter = ranksAfter,
                Outstanding = outstanding,
            };
        }

        private static IEnumerable<int[]> Choices(int n)
        {
            var count = (int)Math.Pow(3, n);
            for (var i = 0; i < count; i++)
            {
                var c = new int[n];
                var v = i;
                for (var k = 0; k < n; k++) { c[k] = v % 3; v /= 3; }
                yield return c;
            }
        }

        private static ClassAbilityDefinition OtherAbility()
        {
            var named = new HashSet<ClassAbilityId>(W.SelectMany(w => w.Rows).Select(r => r.Ability));
            return ClassAbilityRegistry.Abilities.Values
                .Where(d => !named.Contains(d.Id) && d.CostPerRank.Length > 0)
                .OrderBy(d => (int)d.Id)
                .First();
        }

        private static CharacterCapLedger LedgerRow(CapLedgerReason reason, int deltaAvailable, int deltaTotal) => new CharacterCapLedger
        {
            CharacterId = 1,
            CharacterName = "Test",
            Reason = reason.ToCode(),
            DeltaAvailable = deltaAvailable,
            DeltaTotal = deltaTotal,
        };

        private static Func<ClassAbilityId, int> RankOf(Dictionary<ClassAbilityId, int> ranks) =>
            id => ranks.TryGetValue(id, out var r) ? r : 0;

        private static CharacterFacet Facet(byte slot, Dictionary<string, int> abilities) => new CharacterFacet
        {
            CharacterId = 1,
            Slot = slot,
            SkillsJson = "[{\"s\":1}]",
            AbilitiesJson = FacetSnapshot.SerializeAbilities(abilities),
            EquipJson = "[]",
            AttrsJson = "{}",
        };

        private static void AssertToken(ClassAbilityId id, int tier, bool expected, int expectedPrice)
        {
            var d = ClassAbilityRepricing.DecideTokenRefund(0, (int)id, tier, 1);

            Assert.AreEqual(expected, d.Refund, $"{id} tier {tier}: taken back?");
            Assert.AreEqual(expectedPrice, d.UnitPrice, $"{id} tier {tier}: price paid");
        }

        private static void AssertGuarded(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal);
            var to = text.IndexOf(end, from + 1, StringComparison.Ordinal);
            Assert.IsTrue(from >= 0 && to > from, $"could not isolate {start}");

            var body = text.Substring(from, to - from);

            StringAssert.Contains(body, "ClassAbilityRepricePendingFor(", $"{start} must refuse while a reprice is pending");
            StringAssert.Contains(body, "ClassAbilityRepricing.PendingRefusal", $"{start} must use the shared refusal text");
        }

        private static int CountOf(string haystack, string needle)
        {
            var count = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static string ApplyBody()
        {
            var text = Source("Player_ClassAbilities.cs");

            var start = text.IndexOf("public bool ApplyClassAbilityRepricing()", StringComparison.Ordinal);
            var end = text.IndexOf("public bool ClassAbilityRepricePendingFor(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start, "ApplyClassAbilityRepricing not found");

            return text.Substring(start, end - start).Replace("\r\n", "\n");
        }

        private static string Source(string worldObjectFile) =>
            File.ReadAllText(Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(),
                "Source", "ACE.Server", "WorldObjects", worldObjectFile));
    }
}
