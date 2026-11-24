using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Avatars;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The Gauntlet purchase (Task 10): the pure rule order, the reserve / start / release flow against a fake run starter, and
    /// the vendor's panel rows. Tunables go through the GauntletPurchase and AvatarVendor seams, so no PropertyManager key is
    /// read and none needs seeding.
    /// </summary>
    [TestClass]
    public class GauntletPurchaseTests
    {
        private GauntletVendorHarness h;

        [TestInitialize]
        public void Setup() => h = new GauntletVendorHarness();

        [TestCleanup]
        public void Cleanup() => h.Dispose();

        private static readonly GauntletTuning T = GauntletTuning.Defaults with { MinAvatarLevel = 1 };
        private static ulong Price => (ulong)T.Price;

        // ---- Check ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Check_EveryRefusal_InOrder()
        {
            // Everything wrong at once: the first rule in the spec's order wins, then each is fixed in turn.
            Assert.AreEqual(GauntletRefusal.NotPermitted, GauntletPurchase.Check(false, false, 0, true, false, 0, Price, T));
            Assert.AreEqual(GauntletRefusal.Inactive, GauntletPurchase.Check(true, false, 0, true, false, 0, Price, T));

            var picky = T with { MinAvatarLevel = 10 };
            Assert.AreEqual(GauntletRefusal.LevelTooLow, GauntletPurchase.Check(true, true, 9, true, false, 0, Price, picky));
            Assert.AreEqual(GauntletRefusal.InInstance, GauntletPurchase.Check(true, true, 10, true, false, 0, Price, picky));
            Assert.AreEqual(GauntletRefusal.Disabled, GauntletPurchase.Check(true, true, 10, false, false, 0, Price, picky));
            Assert.AreEqual(GauntletRefusal.InsufficientFavor, GauntletPurchase.Check(true, true, 10, false, true, Price - 1, Price, picky));
            Assert.AreEqual(GauntletRefusal.None, GauntletPurchase.Check(true, true, 10, false, true, Price, Price, picky), "exactly the price passes");
        }

        [TestMethod]
        public void Check_Lines_AreAsciiAndDistinct()
        {
            var lines = Enum.GetValues<GauntletRefusal>().Where(r => r != GauntletRefusal.None).Select(r => GauntletPurchase.LineFor(r, T, "x")).ToList();

            Assert.AreEqual(lines.Count, lines.Distinct().Count());
            Assert.IsTrue(lines.All(l => l.All(c => c < 128)));
        }

        // ---- TryBuy -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void TryBuy_Success_ReservesAndLeavesItWithTheRun()
        {
            var state = h.InitActive(favor: 5000);
            var vendor = h.MakeVendor();

            Assert.IsTrue(GauntletPurchase.TryBuy(h.Seneschal, vendor, out var line), line);

            Assert.IsNull(line);
            Assert.AreEqual(1, h.Starter.Started.Count);
            Assert.AreEqual(Price, state.ReservedFavor, "the run owns the reservation now");
            Assert.AreEqual(5000UL, state.Favor, "nothing is committed until wave 1");
        }

        [TestMethod]
        public void TryBuy_StartReceivesNpcSpotAndAvatarId()
        {
            var state = h.InitActive(favor: 5000);
            var vendor = h.MakeVendor();

            Assert.IsTrue(GauntletPurchase.TryBuy(h.Seneschal, vendor, out _));

            var req = h.Starter.Started.Single();
            Assert.AreEqual(GauntletVendorHarness.AvatarId, req.AvatarId);
            Assert.AreEqual((uint?)GauntletVendorHarness.Monarch, req.MonarchGuid);
            Assert.AreEqual(GauntletVendorHarness.VassalGuid, req.BuyerGuid);
            Assert.AreEqual(Price, req.Price);
            Assert.AreEqual(GauntletVendorHarness.Today, req.NowUtc);
            Assert.IsNotNull(req.NpcLocation);
            Assert.AreEqual(vendor.Location.Cell, req.NpcLocation.Cell);
            Assert.AreEqual(vendor.Location.PositionX, req.NpcLocation.PositionX);
            Assert.AreEqual(vendor.Location.PositionY, req.NpcLocation.PositionY);
            Assert.AreEqual(vendor.Location.PositionZ, req.NpcLocation.PositionZ);
            Assert.AreNotSame(vendor.Location, req.NpcLocation, "the request carries a copy, never the NPC's own position object");
        }

        [TestMethod]
        public void Purchase_AbortBeforeWave1_ReleasesReservation()
        {
            var state = h.InitActive(favor: 5000);
            var vendor = h.MakeVendor();
            h.Starter.StartRefusal = "No arena is configured.";

            Assert.IsFalse(GauntletPurchase.TryBuy(h.Seneschal, vendor, out var line));

            StringAssert.Contains(line, "No arena is configured.");
            StringAssert.Contains(line, "No Favor was spent");
            Assert.AreEqual(0UL, state.ReservedFavor, "a false TryStart returns the reservation");
            Assert.AreEqual(5000UL, state.Favor);
            Assert.AreEqual(1, h.Starter.StartCalls);
        }

        [TestMethod]
        public void Purchase_StarterThrows_ReleasesReservation()
        {
            var state = h.InitActive(favor: 5000);
            h.Starter.ThrowOnStart = true;

            Assert.IsFalse(GauntletPurchase.TryBuy(h.Seneschal, h.MakeVendor(), out var line));

            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.IsTrue(line.All(c => c < 128));
        }

        [TestMethod]
        public void TryBuy_NoStarter_RefusesBeforeAnyFavorMoves()
        {
            var state = h.InitActive(favor: 5000);
            GauntletRunStarter.Current = null;

            Assert.IsFalse(GauntletPurchase.TryBuy(h.Seneschal, h.MakeVendor(), out var line));

            Assert.AreEqual(GauntletPurchase.NotAvailableLine, line);
            Assert.AreEqual(0UL, state.ReservedFavor);
        }

        [TestMethod]
        public void TryBuy_StarterCheckRefuses_NoReserveNoStart()
        {
            var state = h.InitActive(favor: 5000);
            h.Starter.CheckRefusal = "That Avatar is on cooldown for 3 hours.";

            Assert.IsFalse(GauntletPurchase.TryBuy(h.Seneschal, h.MakeVendor(), out var line));

            Assert.AreEqual("That Avatar is on cooldown for 3 hours.", line);
            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.AreEqual(0, h.Starter.StartCalls);
        }

        [TestMethod]
        public void TryBuy_PureRefusals_NeverReachTheStarter()
        {
            h.InitActive(favor: 5000);

            Assert.IsFalse(GauntletPurchase.TryBuy(h.Plain, h.MakeVendor(), out var noRights));
            Assert.AreEqual(GauntletPurchase.LineFor(GauntletRefusal.NotPermitted, T), noRights);

            Assert.IsFalse(GauntletPurchase.TryBuy(h.Outsider, h.MakeVendor(), out _));

            Assert.IsFalse(GauntletPurchase.TryBuy(h.Seneschal, h.MakeVendor(instance: 0x80010001u), out var inInstance));
            Assert.AreEqual(GauntletPurchase.LineFor(GauntletRefusal.InInstance, T), inInstance);

            h.GT = T with { Enabled = false };
            Assert.IsFalse(GauntletPurchase.TryBuy(h.Seneschal, h.MakeVendor(), out var off));
            Assert.AreEqual(GauntletPurchase.LineFor(GauntletRefusal.Disabled, T), off);

            h.GT = T with { Price = 6000 };
            Assert.IsFalse(GauntletPurchase.TryBuy(h.Seneschal, h.MakeVendor(), out var poor));
            Assert.AreEqual(GauntletPurchase.LineFor(GauntletRefusal.InsufficientFavor, T), poor);

            Assert.AreEqual(0, h.Starter.CheckCalls, "no state-dependent check ran for any of them");
            Assert.AreEqual(0, h.Starter.StartCalls);
        }

        [TestMethod]
        public void Purchase_ConcurrentBuys_OneRunOneDebit()
        {
            // Plenty of Favor, so the reservation alone would let several buys through: the starter's one-run rule is what stops
            // them, and each loser must hand its reservation back.
            var state = h.InitActive(favor: 20_000);
            var vendor = h.MakeVendor();
            const int Buyers = 8;
            var gate = new Barrier(Buyers);
            var results = new bool[Buyers];

            Parallel.For(0, Buyers, new ParallelOptions { MaxDegreeOfParallelism = Buyers }, i =>
            {
                gate.SignalAndWait();
                results[i] = GauntletPurchase.TryBuy(h.Seneschal, vendor, out _);
            });

            Assert.AreEqual(1, results.Count(r => r), "exactly one buy wins");
            Assert.AreEqual(1, h.Starter.Started.Count);
            Assert.AreEqual(Price, state.ReservedFavor, "only the winner's reservation is still held");

            AvatarManager.CommitDebit(GauntletVendorHarness.AvatarId, Price);

            Assert.AreEqual(20_000UL - Price, state.Favor, "one debit, not several");
            Assert.AreEqual(0UL, state.ReservedFavor);
        }

        // ---- vendor ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Panel_SpenderSeesThreeRows_MemberSeesNone()
        {
            h.InitActive(favor: 5000);
            var vendor = h.MakeVendor();

            var rows = GauntletVendorHarness.Panel(vendor, h.Seneschal);

            CollectionAssert.AreEqual(new[] { AvatarVendor.QuietName, AvatarVendor.AnnouncedName, AvatarVendor.GauntletName }, rows.Select(r => r.Name).ToArray());
            Assert.AreEqual((int)T.Price, rows[2].Value);
            Assert.AreEqual(rows.Count, vendor.GetItemsForSaleCount(h.Seneschal));
            Assert.AreEqual(3, GauntletVendorHarness.Panel(vendor, h.Monarchp).Count, "the monarch spends too");

            Assert.AreEqual(0, GauntletVendorHarness.Panel(vendor, h.Plain).Count, "a member without rights sees none");
            Assert.AreEqual(0, GauntletVendorHarness.Panel(vendor, h.Outsider).Count);
        }

        [TestMethod]
        public void Panel_GauntletRowHidden_WithoutStarterOrWhileSwitchedOff()
        {
            h.InitActive(favor: 5000);
            var vendor = h.MakeVendor();

            GauntletRunStarter.Current = null;
            Assert.AreEqual(2, GauntletVendorHarness.Panel(vendor, h.Seneschal).Count, "no manager, no Gauntlet row");

            GauntletRunStarter.Current = h.Starter;
            h.GT = T with { Enabled = false };
            Assert.AreEqual(2, GauntletVendorHarness.Panel(vendor, h.Seneschal).Count);

            h.GT = T;
            Assert.AreEqual(3, GauntletVendorHarness.Panel(vendor, h.Seneschal).Count);
        }

        [TestMethod]
        public void Buy_GauntletRow_RoutesToGauntletHandler_EventRowsDoNot()
        {
            h.InitActive(favor: 5000);
            var vendor = h.MakeVendor();
            var rows = GauntletVendorHarness.Panel(vendor, h.Seneschal);
            var gauntletCalls = 0;
            var eventCalls = new List<bool>();
            AvatarVendor.GauntletBuyHandler = (Player b, AvatarVendor n, out string l) => { gauntletCalls++; l = null; return true; };
            AvatarVendor.BuyHandler = (Player b, AvatarVendor n, bool announced, out string l) => { eventCalls.Add(announced); l = null; return true; };

            Assert.IsTrue(vendor.BuyItems_ValidateTransaction(new List<ItemProfile> { new ItemProfile(1, rows[2].Guid.Full) }, h.Seneschal));
            Assert.IsTrue(vendor.BuyItems_ValidateTransaction(new List<ItemProfile> { new ItemProfile(1, rows[0].Guid.Full) }, h.Seneschal));
            Assert.IsTrue(vendor.BuyItems_ValidateTransaction(new List<ItemProfile> { new ItemProfile(1, rows[1].Guid.Full) }, h.Seneschal));

            Assert.AreEqual(1, gauntletCalls);
            CollectionAssert.AreEqual(new[] { false, true }, eventCalls);
        }

        [TestMethod]
        public void Buy_GauntletRow_RefusedForMemberWithoutRights()
        {
            h.InitActive(favor: 5000);
            var vendor = h.MakeVendor();
            var rows = GauntletVendorHarness.Panel(vendor, h.Seneschal);

            // A plain member replays the Gauntlet row's guid: the vendor re-checks rights at the moment of sale.
            Assert.IsFalse(vendor.BuyItems_ValidateTransaction(new List<ItemProfile> { new ItemProfile(1, rows[2].Guid.Full) }, h.Plain));
            Assert.AreEqual(0, h.Starter.StartCalls);
        }
    }
}
