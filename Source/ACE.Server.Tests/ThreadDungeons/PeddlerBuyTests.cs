using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.ThreadDungeons.PeddlerTestFixtures;
using static ACE.Server.Tests.ThreadDungeons.ThreadPeddlerVendorTests;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Wandering Peddler buy, end to end at the vendor (owner ruling 2026-11-20, purchases hit the inventory at purchase
    /// time): the payment leaves the pile and the purchase reaches the pack in the same buy; a full pack or too much burden
    /// charges nothing and leaves nothing built; one unbuildable record refuses the whole basket; real builds (stacks above
    /// MaxStackSize, salvage bags) arrive as built; an object the pack refuses after payment waits in the pile; and two
    /// concurrent buys pay once.
    ///
    /// Constructing the vendor needs ThreadPeddlerVendorTests' constructibility seed and its one PropertyManager key
    /// (vendor_shop_uses_generator), restored to its shipped default in TestCleanup, so the class passes alone. Every other
    /// seam is per peddler instance, except the prize and Knots factories, which PeddlerTestFixtures.WithSeams swaps and
    /// restores in a finally (and which restore anything a body overrides inside it).
    /// </summary>
    [TestClass]
    public class PeddlerBuyTests
    {
        private const string ShopGeneratorKey = "vendor_shop_uses_generator";

        [ClassInitialize]
        public static void PrimePlayerType(TestContext _)
        {
            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        [TestInitialize]
        public void Setup()
        {
            SeedConstructible();
        }

        [TestCleanup]
        public void Cleanup()
        {
            PropertyManager.ModifyBool(ShopGeneratorKey, DefaultPropertyManager.DefaultBooleanProperties[ShopGeneratorKey].Item);
        }

        private static List<ItemProfile> One(WorldObject display, int amount = 1) => new List<ItemProfile> { new ItemProfile(amount, display.Guid.Full) };

        private static int Eligible(Stall stall, uint guid) => Pile(stall.Run, guid).Count(ThreadPeddlerPayment.ItemEligibleForPayment);

        private static Stackable StackOf(uint wcid, int max)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.StackSize, 1 },
                    { PropertyInt.MaxStackSize, max },
                    { PropertyInt.StackUnitEncumbrance, 1 },
                    { PropertyInt.StackUnitValue, 1 },
                },
            };

            return new Stackable(weenie, new ObjectGuid(ThreadLootTestFixtures.NextGuid()));
        }

        [TestMethod]
        public void A_buy_takes_exactly_the_price_from_the_pile_and_puts_the_purchase_in_the_pack()
        {
            var stall = ArmedStall(400);
            var buyer = stall.Buyer;
            var before = Pile(stall.Run, buyer);

            var result = stall.Peddler.TryBuy(buyer, true, One(stall.Notes, 3));

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, result.Outcome);
            Assert.AreEqual(150, result.PaymentItems, "exactly the price in payment items");

            var after = Pile(stall.Run, buyer);
            Assert.AreEqual(400 - 150, after.Count, "the pile lost exactly the price, at the buy");
            var paid = before.Where(h => !after.Contains(h)).ToList();
            Assert.AreEqual(150, paid.Count);
            Assert.IsTrue(paid.All(h => h.Item.IsDestroyed), "the payment items are destroyed");

            var given = stall.Pack(buyer).GivenSnapshot();
            Assert.AreEqual(3, given.Count, "the three purchases are in the pack now");
            Assert.IsTrue(given.All(o => !o.IsDestroyed && o.WeenieClassId == NoteWcid));
            Assert.AreEqual(0, after.Count(h => h.PeddlerPurchase), "nothing waits in the pile");
            CollectionAssert.AreEqual(new[] { NoteEntry.Id, NoteEntry.Id, NoteEntry.Id }, stall.Run.PeddlerBought(buyer).ToArray());
        }

        [TestMethod]
        public void A_full_pack_or_too_much_burden_charges_nothing_and_leaves_nothing_built()
        {
            foreach (var (shrink, expected, message) in new (Action<FakePack>, ThreadPeddlerVendor.BuyOutcome, string)[]
            {
                (p => p.FreeInventorySlots = 2, ThreadPeddlerVendor.BuyOutcome.NoPackSpace, ThreadPeddlerVendor.NoPackSpaceMessage),
                (p => p.AvailableBurden = 25, ThreadPeddlerVendor.BuyOutcome.TooEncumbered, ThreadPeddlerVendor.TooEncumberedMessage),
            })
            {
                var stall = ArmedStall(400);
                var buyer = stall.P(stall.Buyer);
                var inner = stall.Peddler.Builder;
                stall.Peddler.Builder = (r, p, rng) => { var l = inner(r, p, rng); foreach (var o in l) o.EncumbranceVal = 10; return l; };
                shrink(stall.Pack(stall.Buyer));

                var result = stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes, 3));

                Assert.AreEqual(expected, result.Outcome);
                Assert.AreEqual(400, Eligible(stall, stall.Buyer), $"{expected}: nothing charged");
                Assert.AreEqual(3, stall.RecordOf.Count, $"{expected}: fixture: three objects were built");
                Assert.IsTrue(stall.RecordOf.Keys.All(o => o.IsDestroyed), $"{expected}: nothing left built");
                Assert.AreEqual(0, stall.Pack(stall.Buyer).GivenSnapshot().Count);
                Assert.AreEqual(0, stall.Run.PeddlerBought(stall.Buyer).Count);

                Assert.IsFalse(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Notes, 3), buyer));
                Assert.AreEqual(message, stall.Told.Last(), $"{expected}: the retail refusal line");
                Assert.AreEqual(400, Eligible(stall, stall.Buyer));
            }

            // Positive control: the same buy with room goes through.
            var roomy = ArmedStall(400);
            var innerRoomy = roomy.Peddler.Builder;
            roomy.Peddler.Builder = (r, p, rng) => { var l = innerRoomy(r, p, rng); foreach (var o in l) o.EncumbranceVal = 10; return l; };
            roomy.Pack(roomy.Buyer).FreeInventorySlots = 3;
            roomy.Pack(roomy.Buyer).AvailableBurden = 30;
            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, roomy.Peddler.TryBuy(roomy.Buyer, true, One(roomy.Notes, 3)).Outcome, "exactly enough room");
        }

        [TestMethod]
        public void One_unbuildable_record_refuses_the_whole_basket()
        {
            WithSeams(seams =>
            {
                var stall = ArmedStall(1000);
                stall.Peddler.Builder = ThreadPeddlerPayment.BuildPurchase;
                seams.Knots = r => (r.KnotLevel, Array.Empty<ACE.Server.Knots.PinnacleDef>()); // the Pattern's pinnacle has left the pool

                var result = stall.Peddler.TryBuy(stall.Buyer, true, new List<ItemProfile> { new ItemProfile(2, stall.Notes.Guid.Full), new ItemProfile(1, stall.Pattern.Guid.Full) });

                Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Unavailable, result.Outcome);
                Assert.AreEqual(ThreadPeddlerVendor.InvalidBuyMessage, ThreadPeddlerVendor.RefusalLine(result.Outcome));
                Assert.AreEqual(2, seams.Objects.Count, "fixture: the two notes were built before the Pattern failed");
                Assert.IsTrue(seams.Objects.All(o => o.IsDestroyed), "what was built is destroyed");
                Assert.AreEqual(1000, Eligible(stall, stall.Buyer), "nothing charged");
                Assert.AreEqual(0, stall.Pack(stall.Buyer).GivenSnapshot().Count);

                // Positive control: the notes alone build and sell.
                Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes, 2)).Outcome);
                Assert.AreEqual(2, stall.Pack(stall.Buyer).GivenSnapshot().Count);
            });
        }

        [TestMethod]
        public void A_quantity_above_MaxStackSize_arrives_as_several_stacks()
        {
            WithSeams(_ =>
            {
                var stall = ArmedStall(400);
                stall.Peddler.Builder = ThreadPeddlerPayment.BuildPurchase;
                ThreadEncounterPrize.CreateObject = w => StackOf(w, 10);
                ThreadPeddlerPayment.EntryLookup = id => id == PlainEntry.Id ? new ThreadEncounterPrizeEntry(PlainEntry.Id, new[] { PlainWcid }, 25, 1) : null;

                var result = stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Plain));

                Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, result.Outcome);
                var given = stall.Pack(stall.Buyer).GivenSnapshot();
                CollectionAssert.AreEqual(new[] { 10, 10, 5 }, given.Select(o => o.StackSize ?? 1).ToArray(), "25 at a max of 10: three stacks, all in the pack");
                Assert.AreEqual(400 - 240, Eligible(stall, stall.Buyer));
            });
        }

        [TestMethod]
        public void A_salvage_purchase_arrives_as_bags_with_their_quality_fields()
        {
            WithSeams(seams =>
            {
                var stall = ArmedStall(400);
                stall.Peddler.Builder = ThreadPeddlerPayment.BuildPurchase;
                ThreadPeddlerPayment.EntryLookup = id => id == NoteEntry.Id ? new ThreadEncounterPrizeEntry(NoteEntry.Id, SalvageWcids, 1, 1, new ThreadEncounterPrizeSalvage(1, 10, 100, 10)) : null;

                Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes)).Outcome);

                var bags = stall.Pack(stall.Buyer).GivenSnapshot();
                Assert.IsTrue(bags.Count >= 1);
                CollectionAssert.AreEqual(seams.Objects, bags, "every bag built went to the pack");
                Assert.IsTrue(bags.All(b => SalvageWcids.Contains(b.WeenieClassId)));
                Assert.IsTrue(bags.All(b => b.Name == "Salvage (100)" && b.Structure == 100 && b.ItemWorkmanship != null && b.NumItemsInMaterial != null), "full bags with their quality fields");
            });
        }

        [TestMethod]
        public void An_object_the_pack_refuses_after_payment_waits_in_the_pile_and_is_never_destroyed()
        {
            var stall = ArmedStall(400);
            var buyer = stall.Buyer;
            stall.Pack(buyer).Accept = _ => false;

            var result = stall.Peddler.TryBuy(buyer, true, One(stall.Notes, 2));

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, result.Outcome, "paid: the buy stands");
            Assert.AreEqual(2, result.ToPile.Count);
            Assert.AreEqual(0, result.Given.Count);

            var pile = Pile(stall.Run, buyer);
            var waiting = pile.Where(h => h.PeddlerPurchase).ToList();
            CollectionAssert.AreEquivalent(result.ToPile, waiting.Select(h => h.Item).ToList(), "each refused object is in the pile as a peddler purchase");
            Assert.IsTrue(waiting.All(h => !h.Item.IsDestroyed && h.FixedOrder >= ThreadPeddlerPayment.PurchaseFixedOrderBase));
            Assert.IsTrue(waiting.All(h => !ThreadPeddlerPayment.ItemEligibleForPayment(h)), "and never taken as payment");
            Assert.AreEqual(400 - 100, pile.Count(ThreadPeddlerPayment.ItemEligibleForPayment), "the payment was taken");
        }

        /// <summary>
        /// Two buys of the whole balance for one buyer on two threads, both held in the build (past the vendor's pre-check)
        /// until the other arrives: the run's locked charge lets exactly one through. Exactly one Bought and one Insufficient,
        /// one object in the pack, the loser's object destroyed, the pile paid once. Repeated in-process.
        /// </summary>
        [TestMethod]
        public void Two_concurrent_buys_pay_once_and_deliver_once()
        {
            for (var round = 0; round < 20; round++)
            {
                var stall = ArmedStall(50);
                var inner = stall.Peddler.Builder;
                using var barrier = new System.Threading.Barrier(2);
                stall.Peddler.Builder = (r, p, rng) => { Assert.IsTrue(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), "both buys reached the build"); return inner(r, p, rng); };

                var outcomes = new System.Collections.Concurrent.ConcurrentQueue<ThreadPeddlerVendor.BuyOutcome>();
                var a = System.Threading.Tasks.Task.Run(() => outcomes.Enqueue(stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes)).Outcome));
                var b = System.Threading.Tasks.Task.Run(() => outcomes.Enqueue(stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes)).Outcome));
                Assert.IsTrue(System.Threading.Tasks.Task.WaitAll(new[] { a, b }, TimeSpan.FromSeconds(20)), $"round {round}: both finished");

                var all = outcomes.ToArray();
                Assert.AreEqual(1, all.Count(o => o == ThreadPeddlerVendor.BuyOutcome.Bought), $"round {round}: exactly one buy ({string.Join(", ", all)})");
                Assert.AreEqual(1, all.Count(o => o == ThreadPeddlerVendor.BuyOutcome.Insufficient), $"round {round}: the other refused at the charge");
                Assert.AreEqual(1, stall.Pack(stall.Buyer).GivenSnapshot().Count, $"round {round}: one object delivered");
                Assert.AreEqual(2, stall.RecordOf.Count, $"round {round}: both built");
                Assert.AreEqual(1, stall.RecordOf.Keys.Count(o => o.IsDestroyed), $"round {round}: the loser's object destroyed");
                Assert.AreEqual(0, Eligible(stall, stall.Buyer), $"round {round}: paid once");
                Assert.AreEqual(1, stall.Run.PeddlerBought(stall.Buyer).Count);
            }
        }

        // ---- code review of #2265: hand-over failures after payment (F1b, F1c, F3) and the production receiver (F4) ----

        private static ThreadPeddlerVendor.PeddlerReceiver Roomy(Func<WorldObject, bool> give) => new ThreadPeddlerVendor.PeddlerReceiver
        {
            FreeInventorySlots = 10_000,
            FreeContainerSlots = 100,
            AvailableBurden = long.MaxValue,
            Give = give,
        };

        /// <summary>F1b: a Give that places the object (it has a container) and then throws is a hand-over: never piled too.</summary>
        [TestMethod]
        public void A_give_that_throws_after_placing_counts_as_given_and_is_never_piled()
        {
            var stall = ArmedStall(400);
            var placed = new List<WorldObject>();
            stall.Peddler.ReceiverOf = p => Roomy(o => { o.ContainerId = 0x7EC4FFFFu; placed.Add(o); throw new InvalidOperationException("probe: after the item went in"); });

            var result = stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes));

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, result.Outcome);
            Assert.AreEqual(1, placed.Count, "fixture: the give placed it");
            Assert.AreEqual(1, result.Given.Count, "counted as given");
            Assert.AreEqual(0, result.ToPile.Count, "and not piled as well");
            Assert.AreEqual(0, Pile(stall.Run, stall.Buyer).Count(h => h.PeddlerPurchase), "no dupe in the pile");
            Assert.IsFalse(placed[0].IsDestroyed);
        }

        /// <summary>F1b, the other half: a Give that throws before placing leaves the object to the pile.</summary>
        [TestMethod]
        public void A_give_that_throws_before_placing_sends_the_object_to_the_pile()
        {
            var stall = ArmedStall(400);
            stall.Peddler.ReceiverOf = p => Roomy(o => throw new InvalidOperationException("probe: before the item went in"));

            var result = stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes));

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, result.Outcome);
            Assert.AreEqual(0, result.Given.Count);
            Assert.AreEqual(1, result.ToPile.Count, "waits in the pile");
            Assert.AreEqual(1, Pile(stall.Run, stall.Buyer).Count(h => h.PeddlerPurchase && ReferenceEquals(h.Item, result.ToPile[0])));
            Assert.AreEqual(400 - 50, Eligible(stall, stall.Buyer), "the payment was taken");
        }

        /// <summary>F1c: a builder that throws on a later record refuses the basket and destroys what the earlier records built.</summary>
        [TestMethod]
        public void A_builder_that_throws_part_way_destroys_what_it_built_and_charges_nothing()
        {
            var stall = ArmedStall(400);
            var inner = stall.Peddler.Builder;
            var calls = 0;
            stall.Peddler.Builder = (r, p, rng) => ++calls == 1 ? inner(r, p, rng) : throw new InvalidOperationException("probe: the second record");

            var result = stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes, 2));

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Unavailable, result.Outcome);
            Assert.AreEqual(2, calls, "fixture: the second record threw");
            Assert.AreEqual(1, stall.RecordOf.Count, "fixture: the first record built one object");
            Assert.IsTrue(stall.RecordOf.Keys.Single().IsDestroyed, "the first record's object is destroyed");
            Assert.AreEqual(400, Eligible(stall, stall.Buyer), "nothing charged");
            Assert.AreEqual(0, stall.Pack(stall.Buyer).GivenSnapshot().Count);
            Assert.AreEqual(0, stall.Run.PeddlerBought(stall.Buyer).Count);
        }

        /// <summary>
        /// F3: a pile add that throws for one paid object neither loses it nor stops the objects after it, and the payment
        /// items are still destroyed.
        /// </summary>
        [TestMethod]
        public void A_pile_add_that_throws_loses_no_paid_object()
        {
            var stall = ArmedStall(400);
            var buyer = stall.Buyer;
            var before = Pile(stall.Run, buyer);
            stall.Pack(buyer).Accept = _ => false;

            var adds = 0;
            stall.Peddler.PileAdd = (r, g, h) => ++adds == 1 ? throw new InvalidOperationException("probe: the first pile add") : r.AddToHeldPile(g, h);

            var result = stall.Peddler.TryBuy(buyer, true, One(stall.Notes, 3));

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, result.Outcome);
            Assert.AreEqual(3, stall.RecordOf.Count, "fixture: three objects built");
            Assert.AreEqual(3, adds, "the throw did not stop the objects after it");

            var pile = Pile(stall.Run, buyer);
            var waiting = pile.Where(h => h.PeddlerPurchase).Select(h => h.Item).ToList();
            CollectionAssert.AreEquivalent(stall.RecordOf.Keys.ToList(), waiting, "every paid object is in the pile, the one whose add threw included");
            CollectionAssert.AreEquivalent(waiting, result.ToPile);
            Assert.IsTrue(waiting.All(o => !o.IsDestroyed), "none lost");

            var paid = before.Where(h => !pile.Contains(h)).ToList();
            Assert.AreEqual(150, paid.Count);
            Assert.IsTrue(paid.All(h => h.Item.IsDestroyed), "the payment items are destroyed");
        }

        /// <summary>
        /// Re-review of 2c90c7941: a pile add that lands the object and THEN throws. The rescue must see it already in the
        /// pile and add nothing: the object is in the pile exactly once. Mutation-proved (the in-pile check forced false
        /// piles it twice).
        /// </summary>
        [TestMethod]
        public void A_pile_add_that_throws_after_adding_is_never_piled_twice()
        {
            var stall = ArmedStall(400);
            var buyer = stall.Buyer;
            stall.Pack(buyer).Accept = _ => false;
            stall.Peddler.PileAdd = (r, g, h) => { Assert.IsTrue(r.AddToHeldPile(g, h), "fixture: the add landed"); throw new InvalidOperationException("probe: after the add"); };

            var result = stall.Peddler.TryBuy(buyer, true, One(stall.Notes));

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, result.Outcome);
            var item = stall.RecordOf.Keys.Single();
            Assert.AreEqual(1, Pile(stall.Run, buyer).Count(h => ReferenceEquals(h.Item, item)), "in the pile exactly once");
            Assert.AreEqual(1, Pile(stall.Run, buyer).Count(h => h.PeddlerPurchase), "no second entry");
            CollectionAssert.AreEqual(new[] { item }, result.ToPile);
            Assert.AreEqual(0, result.Given.Count);
            Assert.IsFalse(item.IsDestroyed);
        }

        /// <summary>
        /// Re-review of 2c90c7941: a Give that returned false but left the object in a container, then a pile add that
        /// throws. The rescue must count it as given and pile nothing. Mutation-proved (the container check forced false
        /// piles an object the pack holds).
        /// </summary>
        [TestMethod]
        public void An_object_left_in_a_container_is_counted_given_and_never_piled_by_the_rescue()
        {
            var stall = ArmedStall(400);
            var buyer = stall.Buyer;
            stall.Peddler.ReceiverOf = p => Roomy(o => { o.ContainerId = 0x7EC4FFFEu; return false; });
            stall.Peddler.PileAdd = (r, g, h) => throw new InvalidOperationException("probe: the pile add");

            var result = stall.Peddler.TryBuy(buyer, true, One(stall.Notes));

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, result.Outcome);
            var item = stall.RecordOf.Keys.Single();
            CollectionAssert.AreEqual(new[] { item }, result.Given, "counted as given");
            Assert.AreEqual(0, result.ToPile.Count);
            Assert.AreEqual(0, Pile(stall.Run, buyer).Count(h => h.PeddlerPurchase), "nothing in the pile");
            Assert.IsFalse(item.IsDestroyed);
        }

        /// <summary>F4: the production receiver. No player, no receiver; a player without a session is never given anything.</summary>
        [TestMethod]
        public void The_production_receiver_needs_a_player_and_never_gives_without_a_session()
        {
            Assert.IsNull(ThreadPeddlerVendor.PeddlerReceiver.For(null));

            // The bare test Player has no attributes, so For() cannot read its burden; the hand-over For() installs is
            // GiveTo, tested directly on a session-less player.
            var player = Viewer(0x50000777u);
            Assert.IsNull(player.Session, "fixture: a session-less player");

            var item = Generic();
            Assert.IsFalse(ThreadPeddlerVendor.PeddlerReceiver.GiveTo(player)(item), "no session: nothing is given");
            Assert.IsNull(item.ContainerId, "and the item was not placed anywhere");
            Assert.IsFalse(ThreadPeddlerVendor.PeddlerReceiver.GiveTo(null)(item), "no player: nothing is given");

            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadPeddlerVendor.cs");
            StringAssert.Contains(src, "Give = GiveTo(player),", "For() hands over through GiveTo");
        }

        /// <summary>A pack read that throws (the production For on a malformed player) refuses the basket and destroys what was built.</summary>
        [TestMethod]
        public void A_pack_read_that_throws_destroys_what_was_built_and_charges_nothing()
        {
            var stall = ArmedStall(400);
            stall.Peddler.ReceiverOf = ThreadPeddlerVendor.PeddlerReceiver.For; // the bare test Player throws reading its burden

            var result = stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes));

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.NotTrading, result.Outcome);
            Assert.AreEqual(1, stall.RecordOf.Count, "fixture: built before the pack read");
            Assert.IsTrue(stall.RecordOf.Keys.All(o => o.IsDestroyed));
            Assert.AreEqual(400, Eligible(stall, stall.Buyer), "nothing charged");
        }

        [TestMethod]
        public void The_buy_line_records_the_payment_items_taken()
        {
            var stall = ArmedStall(400);

            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Notes, 2), stall.P(stall.Buyer)));

            Assert.IsTrue(stall.Run.EncounterOutcomes.Any(l => l.StartsWith("peddler:", StringComparison.Ordinal) && l.Contains("bought 2 x Promissory Note x5 for 100 unit(s)") && l.Contains("100 payment item(s) taken from the pile")),
                string.Join(" | ", stall.Run.EncounterOutcomes));
            CollectionAssert.Contains(stall.Told, "The Wandering Peddler hands you 2 of Promissory Note x5.");
        }
    }
}
