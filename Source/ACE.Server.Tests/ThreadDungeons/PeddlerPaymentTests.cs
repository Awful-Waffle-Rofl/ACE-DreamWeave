using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Knots;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.PeddlerTestFixtures;
using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Wandering Peddler's buy-time payment at the run level (ThreadPeddlerPayment, ThreadDungeonRun.
    /// TryChargePeddlerPurchase): which pile items may pay, the random draw, the charge taking exactly the price from
    /// eligible items under the run's lock, the purchase builds (Fiber and Pattern, salvage bags, the Knots gate), the
    /// clear-pile gate, concurrent charges, and the seams no longer touching the account. Objects come from in-memory
    /// builders through the prize and Knots seams (PeddlerTestFixtures.WithSeams, restored in a finally). No
    /// PropertyManager key is read: runs are built directly.
    /// </summary>
    [TestClass]
    public class PeddlerPaymentTests
    {
        private static PeddlerPurchase Buy(string id, int price, PinnacleDef pattern = null) => new PeddlerPurchase(id, price, pattern);

        /// <summary>A solo run with a plan, cleared, its ledger handed out, shop open, and <paramref name="plain"/> ordinary items in the pile.</summary>
        private static ThreadDungeonRun OpenSolo(int plain)
        {
            var run = ActiveSoloRun();
            GivePlan(run);
            ClearWithPiles(run, plain, run.OwnerGuid);
            OpenShop(run, run.OwnerGuid);
            return run;
        }

        private static PeddlerChargeResult Charge(ThreadDungeonRun run, uint guid, IReadOnlyList<PeddlerPurchase> basket, int seed, out List<HeldPileItem> removed)
            => run.TryChargePeddlerPurchase(guid, basket, (c, n) => ThreadPeddlerPayment.PickPaymentItems(c, n, new Random(seed)), out removed);

        // ---- eligibility and the draw -------------------------------------------------------------------------------

        [TestMethod]
        public void ItemEligibleForPayment_refuses_a_normal_drop_trade_note()
        {
            var note = Generic();
            note.ItemType = ItemType.PromissoryNote;

            // Not FIXED (no FixedOrder), so only the item-type guard can refuse it.
            Assert.IsFalse(ThreadPeddlerPayment.ItemEligibleForPayment(new HeldPileItem(note, null, 1)), "a Trade Note that dropped outside the boss bonus");
        }

        [TestMethod]
        public void Only_ordinary_loot_is_eligible_for_payment()
        {
            Assert.IsTrue(ThreadPeddlerPayment.ItemEligibleForPayment(new HeldPileItem(Generic(), null, 1)), "an ordinary item");

            Assert.IsFalse(ThreadPeddlerPayment.ItemEligibleForPayment(new HeldPileItem(Generic(), null, 1, null, 0)), "the fixed boss bonus (FixedOrder 0)");
            Assert.IsFalse(ThreadPeddlerPayment.ItemEligibleForPayment(new HeldPileItem(Generic(), null, 1, null, 2)), "any FixedOrder");
            Assert.IsFalse(ThreadPeddlerPayment.ItemEligibleForPayment(new HeldPileItem(Generic(KnotItems.FiberWcid, KnotItemKind.Fiber), null, 1)), "a Fiber");
            Assert.IsFalse(ThreadPeddlerPayment.ItemEligibleForPayment(new HeldPileItem(Generic(KnotItems.PatternWcid, KnotItemKind.Pattern), null, 1)), "a Pattern");
            Assert.IsFalse(ThreadPeddlerPayment.ItemEligibleForPayment(new HeldPileItem(Generic(KnotItems.KnotWcid, KnotItemKind.Knot), null, 1)), "a Knot");
            Assert.IsFalse(ThreadPeddlerPayment.ItemEligibleForPayment(new HeldPileItem(Generic(), "Finder", 1)), "a rare");
            Assert.IsFalse(ThreadPeddlerPayment.ItemEligibleForPayment(new HeldPileItem(Generic(), null, 1, peddlerPurchase: true)), "a peddler purchase");
            Assert.IsFalse(ThreadPeddlerPayment.ItemEligibleForPayment(new HeldPileItem(null, null, 1)), "no item");

            var gone = Generic();
            gone.Destroy();
            Assert.IsFalse(ThreadPeddlerPayment.ItemEligibleForPayment(new HeldPileItem(gone, null, 1)), "a destroyed item");
        }

        [TestMethod]
        public void The_draw_takes_exactly_n_distinct_items_or_every_one_when_short()
        {
            var pool = Enumerable.Range(0, 10).ToList();

            for (var seed = 0; seed < 50; seed++)
            {
                var picked = ThreadPeddlerPayment.PickPaymentItems(pool, 4, new Random(seed));
                Assert.AreEqual(4, picked.Count);
                Assert.AreEqual(4, picked.Distinct().Count(), "without replacement");
                Assert.IsTrue(picked.All(pool.Contains));
            }

            CollectionAssert.AreEquivalent(pool, ThreadPeddlerPayment.PickPaymentItems(pool, 25, new Random(1)), "short: every item");
            Assert.AreEqual(0, ThreadPeddlerPayment.PickPaymentItems(pool, 0, new Random(1)).Count);
            Assert.AreEqual(0, ThreadPeddlerPayment.PickPaymentItems(pool, -3, new Random(1)).Count);
            CollectionAssert.AreEqual(Enumerable.Range(0, 10).ToList(), pool, "the input is not changed");

            // Uniform enough to reach every item.
            var seen = new HashSet<int>();
            for (var seed = 0; seed < 200; seed++)
                seen.UnionWith(ThreadPeddlerPayment.PickPaymentItems(pool, 1, new Random(seed)));
            Assert.AreEqual(10, seen.Count);
        }

        // ---- the charge ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_charge_takes_exactly_the_price_from_eligible_items_only()
        {
            var run = OpenSolo(6);
            var owner = run.OwnerGuid;
            var plain = Pile(run, owner);

            var bonus = new HeldPileItem(Generic(), null, 1, null, 0);
            var fiber = new HeldPileItem(Generic(KnotItems.FiberWcid, KnotItemKind.Fiber), null, 1);
            var rare = new HeldPileItem(Generic(), "Finder", 1);
            var earlier = new HeldPileItem(Generic(), null, 1, null, ThreadPeddlerPayment.PurchaseFixedOrderBase, peddlerPurchase: true);
            foreach (var h in new[] { bonus, fiber, rare, earlier })
                Assert.IsTrue(run.AddToHeldPile(owner, h));

            var charged = Charge(run, owner, new[] { Buy("plain", 2), Buy("notes", 1) }, 7, out var removed);

            Assert.IsTrue(charged.IsCharged);
            Assert.AreEqual(3, removed.Count, "exactly the price");
            Assert.IsTrue(removed.All(plain.Contains), "only ordinary loot pays");

            var pile = Pile(run, owner);
            Assert.AreEqual(6 + 4 - 3, pile.Count);
            Assert.IsTrue(pile.Contains(bonus) && pile.Contains(fiber) && pile.Contains(rare) && pile.Contains(earlier), "fixed, Knot, rare and purchased items are never taken");
            Assert.AreEqual(3, charged.Balance, "the balance is the eligible count left");
            CollectionAssert.AreEqual(new[] { "plain", "notes" }, run.PeddlerBought(owner).ToArray());
        }

        [TestMethod]
        public void A_short_pile_is_refused_whole_with_nothing_taken()
        {
            var run = OpenSolo(3);
            var owner = run.OwnerGuid;
            Assert.IsTrue(run.AddToHeldPile(owner, new HeldPileItem(Generic(), null, 1, null, 0)), "a bonus item never makes up the shortfall");

            var charged = Charge(run, owner, new[] { Buy("plain", 4) }, 3, out var removed);

            Assert.AreEqual(PeddlerChargeOutcome.Insufficient, charged.Outcome);
            Assert.AreEqual(3, charged.Balance);
            Assert.AreEqual(0, removed.Count);
            Assert.AreEqual(4, Pile(run, owner).Count, "nothing taken");
            Assert.AreEqual(0, run.PeddlerBought(owner).Count);
        }

        /// <summary>
        /// Code review of #2265 (F1a): a payment chooser that hands back a duplicate, the wrong count, or an item that is not
        /// a current eligible pile item is refused as Invalid with nothing taken. Mutation-proved: with the chooser
        /// validation disabled, each case removes the wrong items.
        /// </summary>
        [TestMethod]
        public void A_chooser_returning_a_duplicate_a_wrong_count_or_an_ineligible_item_is_refused()
        {
            var cases = new (string Name, Func<IReadOnlyList<HeldPileItem>, int, HeldPileItem, IReadOnlyList<HeldPileItem>> Choose)[]
            {
                ("a duplicate", (c, n, bonus) => c.Take(n - 1).Append(c[0]).ToList()),
                ("one too few", (c, n, bonus) => c.Take(n - 1).ToList()),
                ("one too many", (c, n, bonus) => c.Take(n + 1).ToList()),
                ("an ineligible item", (c, n, bonus) => c.Take(n - 1).Append(bonus).ToList()),
                ("an item not in the pile", (c, n, bonus) => c.Take(n - 1).Append(new HeldPileItem(Generic(), null, 1)).ToList()),
                ("a null item", (c, n, bonus) => c.Take(n - 1).Append(null).ToList()),
            };

            foreach (var (name, choose) in cases)
            {
                var run = OpenSolo(6);
                var owner = run.OwnerGuid;
                var bonus = new HeldPileItem(Generic(), null, 1, null, 0);
                Assert.IsTrue(run.AddToHeldPile(owner, bonus));
                var before = Pile(run, owner);

                var charged = run.TryChargePeddlerPurchase(owner, new[] { Buy("plain", 3) }, (c, n) => choose(c, n, bonus), out var removed);

                Assert.AreEqual(PeddlerChargeOutcome.Invalid, charged.Outcome, name);
                Assert.AreEqual(0, removed.Count, $"{name}: nothing removed");
                CollectionAssert.AreEquivalent(before, Pile(run, owner), $"{name}: the pile is unchanged");
                Assert.AreEqual(0, run.PeddlerBought(owner).Count, $"{name}: nothing recorded");
            }

            // Positive control: a correct chooser on the same shape is charged.
            var ok = OpenSolo(6);
            Assert.IsTrue(ok.TryChargePeddlerPurchase(ok.OwnerGuid, new[] { Buy("plain", 3) }, (c, n) => c.Take(n).ToList(), out var taken).IsCharged);
            Assert.AreEqual(3, taken.Count);
        }

        [TestMethod]
        public void A_run_that_ended_charges_nothing()
        {
            var run = OpenSolo(4);
            var pile = Pile(run, run.OwnerGuid);

            Assert.IsTrue(run.MarkEnded("test"));
            ThreadLootPool.DisposeForRunEnd(run);

            Assert.AreEqual(PeddlerChargeOutcome.ShopNotOpen, Charge(run, run.OwnerGuid, new[] { Buy("plain", 1) }, 1, out var removed).Outcome);
            Assert.AreEqual(0, removed.Count);
            Assert.IsTrue(pile.All(h => h.Item.IsDestroyed), "the existing drain destroys the pile; nothing peddler-side is left to drop");
        }

        /// <summary>
        /// Two charges for one buyer released together on two threads, against a pile holding exactly one price: exactly one
        /// is Charged and one Insufficient, the payment is never taken twice, and no item is in two places. Repeated in-process.
        /// </summary>
        [TestMethod]
        public void Two_concurrent_charges_for_one_buyer_succeed_exactly_once()
        {
            for (var round = 0; round < 50; round++)
            {
                var run = OpenSolo(5);
                var owner = run.OwnerGuid;
                var outcomes = new System.Collections.Concurrent.ConcurrentQueue<(PeddlerChargeOutcome Outcome, List<HeldPileItem> Removed)>();
                using var barrier = new System.Threading.Barrier(2);

                void ChargeOnce(int seed)
                {
                    barrier.SignalAndWait();
                    var r = Charge(run, owner, new[] { Buy("plain", 5) }, seed, out var removed);
                    outcomes.Enqueue((r.Outcome, removed));
                }

                var t1 = new System.Threading.Thread(() => ChargeOnce(1));
                var t2 = new System.Threading.Thread(() => ChargeOnce(2));
                t1.Start();
                t2.Start();
                Assert.IsTrue(t1.Join(TimeSpan.FromSeconds(10)) && t2.Join(TimeSpan.FromSeconds(10)), $"round {round}: both threads finished");

                var all = outcomes.ToArray();
                Assert.AreEqual(1, all.Count(o => o.Outcome == PeddlerChargeOutcome.Charged), $"round {round}: exactly one charge ({string.Join(", ", all.Select(o => o.Outcome))})");
                Assert.AreEqual(1, all.Count(o => o.Outcome == PeddlerChargeOutcome.Insufficient), $"round {round}: the other is refused");
                Assert.AreEqual(5, all.Sum(o => o.Removed.Count), $"round {round}: one price removed in all");
                Assert.AreEqual(5, all.SelectMany(o => o.Removed).Distinct().Count(), $"round {round}: no item removed twice");
                Assert.AreEqual(0, Pile(run, owner).Count, $"round {round}: the pile paid once");
                Assert.AreEqual(1, run.PeddlerBought(owner).Count, $"round {round}: one purchase recorded");
            }
        }

        // ---- the builds ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Fiber_and_Pattern_purchases_build_at_the_runs_level_and_the_stored_pinnacle()
        {
            WithSeams(seams =>
            {
                var run = ActiveSoloRun();
                run.MarkKnotLevel(140);

                var fibers = ThreadPeddlerPayment.BuildPurchase(run, Buy("fiber", 2), new Random(2));
                var patterns = ThreadPeddlerPayment.BuildPurchase(run, Buy("pattern", 2, Pinnacle), new Random(2));

                CollectionAssert.AreEqual(new[] { 140, 140 }, seams.FiberLevels.ToArray(), "the entry's 2 Fibers, at the run's Knot level");
                Assert.AreEqual(2, fibers.Count);
                Assert.AreEqual(1, seams.Patterns.Count);
                Assert.AreSame(Pinnacle, seams.Patterns[0], "the pinnacle chosen at purchase");
                Assert.AreEqual(1, patterns.Count);
                Assert.IsTrue(fibers.Concat(patterns).All(KnotItems.IsKnotFamily));
            });
        }

        [TestMethod]
        public void Two_salvage_purchases_build_two_independent_bag_draws_with_quality_fields()
        {
            WithSeams(seams =>
            {
                const int seed = 11;

                // A build consumes the Random only in PickFor (BuildObjects draws nothing at 0 waves), so two sequential
                // builds on one Random are two sequential PickFor draws.
                var rng = new Random(seed);
                var first = ThreadPeddlerPayment.PickFor(SalvageEntry, Buy("salvage", 1), null, _ => true, rng);
                var second = ThreadPeddlerPayment.PickFor(SalvageEntry, Buy("salvage", 1), null, _ => true, rng);
                Assert.IsFalse(first.Wcids.SequenceEqual(second.Wcids), "fixture: this seed gives two different draws");

                var run = ActiveSoloRun();
                var build = new Random(seed);
                var bags = ThreadPeddlerPayment.BuildPurchase(run, Buy("salvage", 1), build).Concat(ThreadPeddlerPayment.BuildPurchase(run, Buy("salvage", 1), build)).ToList();

                var expected = first.Wcids.Concat(second.Wcids).ToArray();
                CollectionAssert.AreEqual(expected, seams.Created.ToArray(), "each purchase rolled its own bag count and materials");
                Assert.AreEqual(expected.Length, bags.Count);
                Assert.IsTrue(bags.All(b => b.Name == "Salvage (100)"), "built as full salvage bags");
                Assert.IsTrue(bags.All(b => b.Structure == 100 && b.ItemWorkmanship != null && b.NumItemsInMaterial != null), "with their quality fields");
            });
        }

        [TestMethod]
        public void Knot_purchases_follow_the_Knots_gate_and_an_unknown_entry_builds_nothing()
        {
            WithSeams(seams =>
            {
                var run = ActiveSoloRun();
                run.MarkKnotLevel(140);

                // Fiber disabled (the gate closed) although the run stamped a level.
                seams.Knots = r => (null, Array.Empty<PinnacleDef>());
                Assert.AreEqual(0, ThreadPeddlerPayment.BuildPurchase(run, Buy("fiber", 3), new Random(1)).Count);
                Assert.AreEqual(0, seams.FiberLevels.Count);

                // A Pattern whose pinnacle has left the pool.
                seams.Knots = r => (r.KnotLevel, new[] { new PinnacleDef { Key = "other", Name = "Other" } });
                Assert.AreEqual(0, ThreadPeddlerPayment.BuildPurchase(run, Buy("pattern", 3, Pinnacle), new Random(1)).Count);
                Assert.AreEqual(0, seams.Patterns.Count);

                Assert.AreEqual(0, ThreadPeddlerPayment.BuildPurchase(run, Buy("gone", 1), new Random(1)).Count, "no such entry");
                Assert.AreEqual(1, ThreadPeddlerPayment.BuildPurchase(run, Buy("plain", 1), new Random(1)).Count, "positive control");

                // The production seam is the encounter prize's own gate.
                var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadPeddlerPayment.cs");
                StringAssert.Contains(src, "KnotsInputs = ThreadEncounterPrize.KnotsInputs;");
            });
        }

        [TestMethod]
        public void PackNeeds_counts_burden_and_slots_the_way_a_retail_buy_does()
        {
            var a = Generic();
            a.EncumbranceVal = 30;
            var b = Generic();
            b.EncumbranceVal = 12;

            var pack = new Container(new ACE.Entity.Models.Weenie { WeenieClassId = 136, WeenieType = WeenieType.Container, PropertiesInt = new Dictionary<ACE.Entity.Enum.Properties.PropertyInt, int>() }, new ACE.Entity.ObjectGuid(NextGuid()));
            pack.EncumbranceVal = 5;
            Assert.IsTrue(pack.UseBackpackSlot, "fixture: a pack takes a container slot");

            var (inventory, container, burden) = ThreadPeddlerPayment.PackNeeds(new WorldObject[] { a, b, null, pack });
            Assert.AreEqual(2, inventory);
            Assert.AreEqual(1, container);
            Assert.AreEqual(47, burden);
        }

        // ---- the gate and the seams ---------------------------------------------------------------------------------

        [TestMethod]
        public void The_clear_pile_gate_waits_for_the_rolls_banked_by_the_clear_only()
        {
            var run = ActiveSoloRun();
            Assert.IsFalse(run.PeddlerClearPileComplete, "never before the clear");
            Bank(run, 2);
            Clear(run);
            Assert.IsFalse(run.PeddlerClearPileComplete, "two at-clear rolls still in the ledger");

            Assert.IsTrue(run.TryClaimNextLootEntry(out _));
            Bank(run, 3); // late kills, after the clear
            Assert.IsFalse(run.PeddlerClearPileComplete);

            Assert.IsTrue(run.TryClaimNextLootEntry(out _));
            Assert.IsTrue(run.PeddlerClearPileComplete, "late kills never hold it back");

            Assert.IsTrue(run.TryAddOverflow(Item(), null));
            Assert.IsFalse(run.PeddlerClearPileComplete, "built-but-unpiled loot does");
            run.TakeAllOverflow();
            Assert.IsTrue(run.TryMarkLootBonusPending());
            Assert.IsFalse(run.PeddlerClearPileComplete, "and so does a pending boss bonus");
        }

        [TestMethod]
        public void The_seams_hold_before_any_delivery_and_no_longer_settle()
        {
            var placer = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadCachePlacer.cs");
            var execute = PooledLootSourceText.MethodBody(placer, "private static void Execute(ThreadDungeonRun run, Landblock landblock, CacheRequestMode mode, uint requesterGuid, ThreadLootRollBudget budget)");
            var gate = execute.IndexOf("if (ledgerClaimed < ledgerSnapshot || run.HasUndealtNonLedgerLoot)", StringComparison.Ordinal);
            var hold = execute.IndexOf("if (SoloPeddlerHoldsDelivery(run, ownerInside, () => PeddlerPresent(run, landblock, owner)))", StringComparison.Ordinal);
            var firstPile = execute.IndexOf("if (!run.HasHeldPile(run.OwnerGuid))", StringComparison.Ordinal);
            Assert.IsTrue(gate >= 0 && hold > gate && firstPile > hold, "solo: the snapshot gate, then the peddler hold, then the first pile delivery");
            Assert.AreEqual("return;", execute.Substring(hold).Split('\n')[1].Trim(), "a hold returns before anything else");
            Assert.IsFalse(execute.Contains("TrySettle"), "solo: payment is taken at the buy, never in delivery");

            var group = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadGroupCacheDelivery.cs");
            var serve = PooledLootSourceText.MethodBody(group, "internal static CacheFormPass ServeMemberCore(ThreadDungeonRun run, uint memberGuid, bool topUp, DeliveryWorld world, bool quiet)");
            var gHold = serve.IndexOf("switch (ThreadPeddlerShop.Gate(run, memberGuid,", StringComparison.Ordinal);
            var claim = gHold < 0 ? -1 : serve.IndexOf("run.TryClaimMemberDeliveryWanted(memberGuid);", gHold, StringComparison.Ordinal);
            var firstAny = serve.IndexOf("run.", StringComparison.Ordinal);
            Assert.IsTrue(gHold >= 0 && gHold < firstAny && gHold < claim, "group: the peddler hold is the first thing ServeMemberCore does, before the delivery claim");
            Assert.IsFalse(serve.Contains("TrySettle"), "group: payment is taken at the buy, never in delivery");

            var fill = PooledLootSourceText.MethodBody(group, "internal static CacheFillResult FillFromPile(ThreadDungeonRun run, Container cache, uint memberGuid)");
            Assert.IsFalse(fill.Contains("Peddler"), "never gated inside FillFromPile");
        }
    }
}
