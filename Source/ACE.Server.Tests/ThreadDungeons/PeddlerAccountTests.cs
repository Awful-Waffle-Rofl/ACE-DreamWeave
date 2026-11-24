using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.PeddlerTestFixtures;
using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Wandering Peddler's per-player accounting (ThreadDungeonRun_Peddler.cs), clear only: the real-pile balance,
    /// all-or-nothing buy-time charges gated on an OPEN shop, the per-member shop latch, the hold gate's sequence, the one plan and
    /// one NPC per run, and the banked-roll counter. No PropertyManager key is read: runs are built directly.
    /// </summary>
    [TestClass]
    public class PeddlerAccountTests
    {
        private static PeddlerPurchase Buy(string id, int price) => new PeddlerPurchase(id, price);

        /// <summary>The run's charge with the production random draw (seeded), its removed payment items discarded.</summary>
        private static PeddlerChargeResult Charge(ThreadDungeonRun run, uint guid, IReadOnlyList<PeddlerPurchase> basket)
            => run.TryChargePeddlerPurchase(guid, basket, (c, n) => ThreadPeddlerPayment.PickPaymentItems(c, n, new Random(1)), out _);

        /// <summary>A solo run with a plan, cleared, its ledger handed out, and <paramref name="plain"/> ordinary items in the owner's pile, shop open.</summary>
        private static ThreadDungeonRun OpenSolo(int plain)
        {
            var run = ActiveSoloRun();
            GivePlan(run);
            ClearWithPiles(run, plain, run.OwnerGuid);
            OpenShop(run, run.OwnerGuid);
            return run;
        }

        [TestMethod]
        public void The_clear_balance_counts_only_eligible_items_in_the_real_pile()
        {
            var run = ActiveSoloRun();
            Bank(run, 50, 10);
            var owner = run.OwnerGuid;
            Assert.AreEqual(0, run.PeddlerBalance(owner), "banked rolls are not currency: only the real pile is");

            GivePlan(run);
            ClearWithPiles(run, 3, owner);

            var note = Generic();
            note.ItemType = ACE.Entity.Enum.ItemType.PromissoryNote;

            foreach (var held in new[]
            {
                new HeldPileItem(Generic(), null, 1, null, 0),                                          // the boss bonus (incl. its salvage bags)
                new HeldPileItem(Generic(), "Finder", 1),                                               // a rare
                new HeldPileItem(Generic(ACE.Server.Knots.KnotItems.FiberWcid, ACE.Server.Knots.KnotItemKind.Fiber), null, 1),
                new HeldPileItem(Generic(ACE.Server.Knots.KnotItems.PatternWcid, ACE.Server.Knots.KnotItemKind.Pattern), null, 1),
                new HeldPileItem(note, null, 1),                                                        // a Trade Note outside the bonus
                new HeldPileItem(Generic(), null, 1, peddlerPurchase: true),                            // an earlier purchase
            })
                Assert.IsTrue(run.AddToHeldPile(owner, held));

            Assert.AreEqual(9, Pile(run, owner).Count, "fixture: nine items held");
            Assert.AreEqual(3, run.PeddlerBalance(owner), "only the three ordinary items are currency");

            OpenShop(run, owner);
            Assert.IsTrue(Charge(run, owner, new[] { Buy("plain", 2) }).IsCharged);
            Assert.AreEqual(1, run.PeddlerBalance(owner), "minus the payment the charge removed from the pile");

            AddPlain(run, owner, 4);
            Assert.AreEqual(5, run.PeddlerBalance(owner), "a pile that grows (a late kill) raises it");
            Assert.AreEqual(0, run.PeddlerBalance(0x59999999u), "a non-member has nothing");
        }

        [TestMethod]
        public void Balance_is_zero_without_pooled_loot()
        {
            var run = ActiveSoloRun(pooled: false);
            Assert.IsFalse(run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null, 1, 0)), "a corpse-model run banks nothing");
            AddPlain(run, run.OwnerGuid, 3);
            Assert.AreEqual(0, run.PeddlerBalance(run.OwnerGuid));
        }

        [TestMethod]
        public void A_charge_is_refused_unless_the_members_shop_is_open()
        {
            var run = ActiveSoloRun();
            var owner = run.OwnerGuid;
            GivePlan(run);
            AddPlain(run, owner, 10);
            Assert.AreEqual(PeddlerChargeOutcome.ShopNotOpen, Charge(run, owner, new[] { Buy("plain", 1) }).Outcome, "mid-run: nothing is traded before the clear");

            Clear(run);
            DrainLedger(run);
            Assert.AreEqual(PeddlerChargeOutcome.ShopNotOpen, Charge(run, owner, new[] { Buy("plain", 1) }).Outcome, "cleared, but not presented");

            OpenShop(run, owner);
            Assert.IsTrue(Charge(run, owner, new[] { Buy("plain", 1) }).IsCharged, "open: charged");

            Assert.IsTrue(run.TryMarkShopDone(owner));
            Assert.AreEqual(PeddlerChargeOutcome.ShopNotOpen, Charge(run, owner, new[] { Buy("plain", 1) }).Outcome, "a charge after done is refused");
            Assert.AreEqual(1, run.PeddlerBought(owner).Count);

            var ended = OpenSolo(5);
            Assert.IsTrue(ended.MarkEnded("test"));
            Assert.AreEqual(PeddlerChargeOutcome.ShopNotOpen, Charge(ended, ended.OwnerGuid, new[] { Buy("plain", 1) }).Outcome, "the run ended");
        }

        [TestMethod]
        public void A_basket_is_charged_whole_or_not_at_all_against_the_real_pile()
        {
            var run = OpenSolo(9);
            var owner = run.OwnerGuid;

            var tooMuch = Charge(run, owner, new[] { Buy("plain", 5), Buy("notes", 5) });
            Assert.AreEqual(PeddlerChargeOutcome.Insufficient, tooMuch.Outcome);
            Assert.AreEqual(9, tooMuch.Balance);
            Assert.AreEqual(9, Pile(run, owner).Count, "no part of a refused basket is charged");
            Assert.AreEqual(0, run.PeddlerBought(owner).Count);

            var fits = Charge(run, owner, new[] { Buy("plain", 5), Buy("notes", 4) });
            Assert.AreEqual(PeddlerChargeOutcome.Charged, fits.Outcome);
            Assert.AreEqual(0, fits.Balance);
            CollectionAssert.AreEqual(new[] { "plain", "notes" }, run.PeddlerBought(owner).ToArray());
            Assert.AreEqual(PeddlerChargeOutcome.Insufficient, Charge(run, owner, new[] { Buy("plain", 1) }).Outcome, "never below zero");
        }

        [TestMethod]
        public void One_entry_may_be_bought_again_and_again()
        {
            var run = OpenSolo(18);

            Assert.IsTrue(Charge(run, run.OwnerGuid, new[] { Buy("salvage", 3), Buy("salvage", 3) }).IsCharged, "twice in one basket");
            Assert.IsTrue(Charge(run, run.OwnerGuid, new[] { Buy("salvage", 3) }).IsCharged, "and again later");

            Assert.AreEqual(3, run.PeddlerBought(run.OwnerGuid).Count(p => p == "salvage"));
            Assert.AreEqual(9, run.PeddlerBalance(run.OwnerGuid));
        }

        // ---- one per player (owner ruling 2026-11-20) -------------------------------------------------------------

        /// <summary>A solo run whose plan caps "pattern" and "fiber" at one per player, cleared, shop open.</summary>
        private static ThreadDungeonRun OpenSoloCapped(int plain)
        {
            var run = ActiveSoloRun();
            GivePlan(run, CappedPlan(new[] { "pattern", "fiber" }));
            ClearWithPiles(run, plain, run.OwnerGuid);
            OpenShop(run, run.OwnerGuid);
            return run;
        }

        [TestMethod]
        public void A_capped_entry_charges_once_then_LimitReached_changes_nothing()
        {
            var run = OpenSoloCapped(2000);
            var owner = run.OwnerGuid;
            var other = new ACE.Server.Knots.PinnacleDef { Key = "p2", Name = "P Two" };

            Assert.IsTrue(Charge(run, owner, new[] { new PeddlerPurchase("pattern", 320, Pinnacle) }).IsCharged, "the first Pattern is charged");

            var balance = run.PeddlerBalance(owner);
            var pile = Pile(run, owner).Count;

            // By entry id: a Pattern for a DIFFERENT pinnacle is still a second "pattern".
            var second = Charge(run, owner, new[] { new PeddlerPurchase("pattern", 320, other) });
            Assert.AreEqual(PeddlerChargeOutcome.LimitReached, second.Outcome, "a second pattern, whatever its pinnacle");
            Assert.AreEqual(balance, second.Balance);

            var mixed = Charge(run, owner, new[] { Buy("trade_note_250k", 50), new PeddlerPurchase("pattern", 320, Pinnacle) });
            Assert.AreEqual(PeddlerChargeOutcome.LimitReached, mixed.Outcome, "a basket with one over-cap line is refused whole");

            Assert.AreEqual(balance, run.PeddlerBalance(owner), "balance unchanged");
            Assert.AreEqual(pile, Pile(run, owner).Count, "pile unchanged");
            CollectionAssert.AreEqual(new[] { "pattern" }, run.PeddlerBought(owner).ToArray(), "bought unchanged");
        }

        [TestMethod]
        public void A_basket_of_two_of_one_capped_entry_charges_nothing()
        {
            var run = OpenSoloCapped(2000);
            var owner = run.OwnerGuid;

            Assert.AreEqual(PeddlerChargeOutcome.LimitReached, Charge(run, owner, new[] { Buy("fiber", 120), Buy("fiber", 120) }).Outcome);
            Assert.AreEqual(0, run.PeddlerBought(owner).Count, "nothing charged");
            Assert.AreEqual(2000, Pile(run, owner).Count);
            Assert.AreEqual(2000, run.PeddlerBalance(owner));

            Assert.IsTrue(Charge(run, owner, new[] { Buy("fiber", 120), new PeddlerPurchase("pattern", 320, Pinnacle) }).IsCharged,
                "one each of two different capped entries is within the cap");
        }

        [TestMethod]
        public void An_uncapped_entry_buys_repeatedly_beside_a_cap()
        {
            var run = OpenSoloCapped(500);
            var owner = run.OwnerGuid;

            for (var i = 0; i < 4; i++)
                Assert.IsTrue(Charge(run, owner, new[] { Buy("trade_note_250k", 50) }).IsCharged, $"buy {i + 1}");

            Assert.IsTrue(Charge(run, owner, new[] { Buy("trade_note_250k", 50), Buy("trade_note_250k", 50) }).IsCharged, "twice in one basket");
            Assert.AreEqual(6, run.PeddlerBought(owner).Count(p => p == "trade_note_250k"));
            Assert.AreEqual(200, run.PeddlerBalance(owner));
        }

        /// <summary>
        /// Review of c5f4253d5 (F2): two charges of one capped "pattern" each, released together for the same buyer, end
        /// as exactly one Charged and one LimitReached, because the cap is checked under the run's lock with the charge.
        /// Outcomes are recorded into a ConcurrentQueue. Repeated in-process, and run 20x alone.
        /// </summary>
        [TestMethod]
        public void Two_concurrent_capped_charges_for_one_buyer_charge_exactly_once()
        {
            for (var round = 0; round < 50; round++)
            {
                var run = OpenSoloCapped(2000);
                var owner = run.OwnerGuid;
                var outcomes = new System.Collections.Concurrent.ConcurrentQueue<PeddlerChargeOutcome>();
                using var barrier = new System.Threading.Barrier(2);

                void ChargeOnce()
                {
                    barrier.SignalAndWait();
                    outcomes.Enqueue(Charge(run, owner, new[] { new PeddlerPurchase("pattern", 320, Pinnacle) }).Outcome);
                }

                var t1 = new System.Threading.Thread(ChargeOnce);
                var t2 = new System.Threading.Thread(ChargeOnce);
                t1.Start();
                t2.Start();
                Assert.IsTrue(t1.Join(TimeSpan.FromSeconds(10)) && t2.Join(TimeSpan.FromSeconds(10)), $"round {round}: both threads finished");

                var all = outcomes.ToArray();
                Assert.AreEqual(2, all.Length, $"round {round}: two outcomes");
                Assert.AreEqual(1, all.Count(o => o == PeddlerChargeOutcome.Charged), $"round {round}: exactly one charge ({string.Join(", ", all)})");
                Assert.AreEqual(1, all.Count(o => o == PeddlerChargeOutcome.LimitReached), $"round {round}: exactly one LimitReached");
                Assert.AreEqual(1, run.PeddlerBought(owner).Count, $"round {round}: one purchase recorded");
                Assert.AreEqual(2000 - 320, Pile(run, owner).Count, $"round {round}: one price taken from the pile");
            }
        }

        [TestMethod]
        public void Without_a_cap_a_pattern_buys_again()
        {
            var run = OpenSolo(1000);

            Assert.IsTrue(Charge(run, run.OwnerGuid, new[] { new PeddlerPurchase("pattern", 320, Pinnacle) }).IsCharged);
            Assert.IsTrue(Charge(run, run.OwnerGuid, new[] { new PeddlerPurchase("pattern", 320, Pinnacle) }).IsCharged,
                "the cap is data: a plan whose config names no onePerPlayer entry caps nothing");
        }

        [TestMethod]
        public void Only_a_member_seated_at_the_clear_is_offered_a_shop()
        {
            var run = ActiveGroupRun(Member);
            run.SetMemberKey(Member, 0);
            GivePlan(run);
            ClearWithPiles(run, 5, Owner, Member);

            Assert.AreEqual(PeddlerGate.None, run.PeddlerGateFor(Member), "a keyless member gets no shop");
            Assert.IsTrue(run.IsPeddlerShopDone(Member), "and is latched done");
            Assert.IsFalse(run.TryOpenPeddlerShop(Member));
            Assert.AreEqual(PeddlerChargeOutcome.ShopNotOpen, Charge(run, Member, new[] { Buy("plain", 1) }).Outcome);
            Assert.AreEqual(PeddlerChargeOutcome.ShopNotOpen, Charge(run, 0x59999999u, new[] { Buy("plain", 1) }).Outcome, "a non-member");

            OpenShop(run, Owner);
            Assert.IsTrue(Charge(run, Owner, new[] { Buy("plain", 1) }).IsCharged, "the owner always holds a seat");
        }

        [TestMethod]
        public void A_malformed_basket_is_refused()
        {
            var run = OpenSolo(5);

            Assert.AreEqual(PeddlerChargeOutcome.Invalid, Charge(run, run.OwnerGuid, null).Outcome);
            Assert.AreEqual(PeddlerChargeOutcome.Invalid, Charge(run, run.OwnerGuid, new PeddlerPurchase[0]).Outcome);
            Assert.AreEqual(PeddlerChargeOutcome.Invalid, Charge(run, run.OwnerGuid, new[] { Buy("plain", 0) }).Outcome, "a free purchase");
            Assert.AreEqual(PeddlerChargeOutcome.Invalid, Charge(run, run.OwnerGuid, new[] { Buy("plain", -1) }).Outcome);
            Assert.AreEqual(PeddlerChargeOutcome.Invalid, Charge(run, run.OwnerGuid, new[] { Buy(null, 1) }).Outcome);
            Assert.AreEqual(PeddlerChargeOutcome.Invalid, Charge(run, run.OwnerGuid, new[] { Buy("plain", 1), null }).Outcome);
            Assert.AreEqual(0, run.PeddlerBought(run.OwnerGuid).Count);
        }

        [TestMethod]
        public void TryMarkShopDone_latches_once()
        {
            var run = OpenSolo(5);
            var owner = run.OwnerGuid;

            Assert.IsTrue(run.IsPeddlerShopOpen(owner));
            Assert.IsTrue(run.TryMarkShopDone(owner), "the first done wins");
            Assert.IsFalse(run.TryMarkShopDone(owner), "every later one changes nothing");
            Assert.IsFalse(run.IsPeddlerShopOpen(owner));
            Assert.IsTrue(run.IsPeddlerShopDone(owner));
            Assert.IsFalse(run.TryOpenPeddlerShop(owner), "never reopened");
            Assert.AreEqual(PeddlerGate.None, run.PeddlerGateFor(owner), "a done member delivers as usual");

            var group = ActiveGroupRun(Member);
            GivePlan(group);
            ClearWithPiles(group, 5, Owner, Member);
            Assert.IsFalse(group.TryMarkShopDone(Member), "an undecided member is not open, so there is nothing to release");
            Assert.IsTrue(group.MarkPeddlerShopSkipped(Member));
            Assert.IsFalse(group.TryMarkShopDone(Member), "a skipped member was never open");
        }

        [TestMethod]
        public void The_gate_waits_for_the_pile_then_presents_then_holds_then_releases()
        {
            var run = ActiveSoloRun();
            var owner = run.OwnerGuid;
            Assert.AreEqual(PeddlerGate.None, run.PeddlerGateFor(owner), "no peddler, no gate");

            GivePlan(run);
            Bank(run, 2);
            Assert.AreEqual(PeddlerGate.None, run.PeddlerGateFor(owner), "before the clear the gate never holds");

            Clear(run);
            Assert.AreEqual(PeddlerGate.WaitPile, run.PeddlerGateFor(owner), "cleared, but the at-clear rolls are not handed out");
            Assert.IsFalse(run.TryOpenPeddlerShop(owner), "no shop opens on an incomplete pile");

            DrainLedger(run);
            Assert.AreEqual(PeddlerGate.Present, run.PeddlerGateFor(owner));
            Assert.AreEqual(PeddlerGate.Present, run.PeddlerGateFor(owner), "repeatable: reading the gate decides nothing");

            Assert.IsTrue(run.TryOpenPeddlerShop(owner));
            Assert.AreEqual(PeddlerGate.Held, run.PeddlerGateFor(owner));

            Assert.IsTrue(run.TryMarkShopDone(owner));
            Assert.AreEqual(PeddlerGate.None, run.PeddlerGateFor(owner));
        }

        [TestMethod]
        public void A_charge_takes_its_payment_from_the_pile_at_the_buy()
        {
            var run = OpenSolo(6);
            var owner = run.OwnerGuid;
            var before = Pile(run, owner);

            var charged = run.TryChargePeddlerPurchase(owner, new[] { Buy("plain", 2) }, (c, n) => ThreadPeddlerPayment.PickPaymentItems(c, n, new Random(1)), out var removed);

            Assert.IsTrue(charged.IsCharged);
            Assert.AreEqual(2, removed.Count, "exactly the price removed");
            Assert.AreEqual(2, removed.Distinct().Count());
            Assert.IsTrue(removed.All(before.Contains), "from the buyer's own pile");
            var after = Pile(run, owner);
            Assert.AreEqual(4, after.Count, "the pile is net of the payment AT the buy, while the shop is still open");
            Assert.IsFalse(removed.Any(after.Contains));
            Assert.AreEqual(4, charged.Balance);
            Assert.IsTrue(removed.All(h => !h.Item.IsDestroyed), "the run hands the payment back; the caller destroys it outside the lock");

            Assert.IsTrue(run.TryMarkShopDone(owner));
            Assert.AreEqual(PeddlerChargeOutcome.ShopNotOpen, Charge(run, owner, new[] { Buy("plain", 1) }).Outcome, "done: no further charge");
            Assert.AreEqual(4, Pile(run, owner).Count, "and nothing more is taken");
        }

        [TestMethod]
        public void A_run_takes_one_plan_and_one_npc_ever()
        {
            var run = ActiveSoloRun();
            Assert.IsFalse(run.TrySetPeddlerPlan(TestPlan()), "no claim, no plan");
            Assert.IsTrue(run.TryClaimPeddler());
            Assert.IsFalse(run.TryClaimPeddler(), "one claim");
            Assert.IsTrue(run.TrySetPeddlerPlan(TestPlan()));
            Assert.IsFalse(run.TrySetPeddlerPlan(TestPlan()), "one plan");

            run.ReleasePeddlerClaim();
            Assert.IsTrue(run.PeddlerClaimed, "the claim is kept once a plan is recorded");

            var cleared = ActiveSoloRun();
            Clear(cleared);
            Assert.IsTrue(cleared.TryClaimPeddler());
            Assert.IsFalse(cleared.TrySetPeddlerPlan(TestPlan()), "no plan after the clear");
            cleared.ReleasePeddlerClaim();
            Assert.IsFalse(cleared.PeddlerClaimed, "a claim with no plan is given back");
        }

        [TestMethod]
        public void Closing_the_peddler_releases_open_shops_and_presents_nobody_again()
        {
            var run = ActiveGroupRun(Member, Member2);
            GivePlan(run);
            ClearWithPiles(run, 5, Owner, Member, Member2);
            OpenShop(run, Owner);

            CollectionAssert.AreEqual(new[] { Owner }, run.ClosePeddler(), "the open shop is released");
            Assert.IsTrue(run.IsPeddlerShopDone(Owner));
            Assert.IsTrue(run.PeddlerClosed);
            Assert.AreEqual(PeddlerGate.None, run.PeddlerGateFor(Member), "nobody is presented after the close");
            Assert.IsTrue(run.IsPeddlerShopDone(Member));
            Assert.AreEqual(0, run.ClosePeddler().Count, "idempotent");
        }

        [TestMethod]
        public void Banked_rolls_never_fall_when_the_trickle_drains_the_ledger()
        {
            var run = ActiveSoloRun();
            Bank(run, 3);
            Assert.AreEqual(3, run.BankedLootRolls);

            // What ThreadLootTrickle does before the clear: claim the head of the ledger and park its build as prebuilt.
            Assert.IsTrue(run.TryClaimLootRolls(1, true, out var piece));
            Assert.IsTrue(run.TryAddPrebuilt(piece, new List<ACE.Server.WorldObjects.WorldObject> { Item() }));
            Assert.IsTrue(run.TryClaimNextLootEntry(out _));

            Assert.AreEqual(1, run.LedgerCount, "the ledger drained");
            Assert.AreEqual(3, run.BankedLootRolls, "the banked counter did not");
            Assert.AreEqual(2, run.ClaimedLootRolls);

            // A fat group entry claimed a slice at a time counts every roll once.
            var group = ActiveGroupRun(Member);
            Bank(group, 1, rolls: 5);
            Assert.IsTrue(group.TryClaimLootRolls(2, true, out _));
            Assert.IsTrue(group.TryClaimLootRolls(2, true, out _));
            Assert.IsTrue(group.TryClaimLootRolls(2, true, out _));
            Assert.AreEqual(5, group.BankedLootRolls);
            Assert.AreEqual(5, group.ClaimedLootRolls, "2 + 2 + the final 1");
        }

        [TestMethod]
        public void The_banked_counter_sits_on_the_only_ledger_enqueue_and_claims_are_counted_in_both_branches()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRun_LootPool.cs");
            Assert.AreEqual(1, CountOf(src, "lootLedger.Enqueue("), "one enqueue site");

            var append = PooledLootSourceText.MethodBody(src, "public bool TryAppendLootEntry(ThreadLootLedgerEntry entry)");
            Assert.IsTrue(append.IndexOf("lootLedger.Enqueue(entry);", StringComparison.Ordinal) < append.IndexOf("CountBankedRollsLocked(entry);", StringComparison.Ordinal));

            var claim = PooledLootSourceText.MethodBody(src, "internal bool TryClaimLootRolls(int maxRolls, bool allowPartial, out ThreadLootLedgerEntry piece)");
            StringAssert.Contains(claim, "CountClaimedRollsLocked(remaining);");
            StringAssert.Contains(claim, "CountClaimedRollsLocked(maxRolls);");

            var run = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRun.cs");
            var check = PooledLootSourceText.MethodBody(run, "private void CheckClearedLocked()");
            Assert.IsTrue(check.IndexOf("state = ThreadDungeonRunState.Cleared;", StringComparison.Ordinal) < check.IndexOf("StampPeddlerClearLocked();", StringComparison.Ordinal),
                "the clear stamp is taken at the transition");
        }

        private static int CountOf(string text, string needle)
        {
            var count = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;

            return count;
        }
    }
}
