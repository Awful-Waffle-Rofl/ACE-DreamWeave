using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// The Private World Event purchase: the pure Check rules, and TryBuy's reserve / enqueue / commit-or-refund flow. Every
    /// collaborator that would reach the world is a seam restored in cleanup. Nothing reads PropertyManager: tunables go through
    /// AvatarVendor.Tuning.
    /// </summary>
    [TestClass]
    public class AvatarPrivateEventTests
    {
        private const double Start = 1_800_000_000.0;
        private static readonly DateTime Today = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Now = new DateTime(2027, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        // The event gate is opened to level 1: the fixture Avatar is level 4 on the synthetic chart, below the shipped gate of 40.
        // The pre-scaling flat prices (2000 quiet, 3000 announced) are seeded here: these tests pin the Favor bookkeeping around a
        // price, not the price formula, which the scaled-price tests below and AvatarPricingTests pin.
        private static readonly AvatarTuning T = AvatarTuning.Defaults with
        {
            EventMinLevel = 1,
            EventPriceQuiet = 2000,
            EventPriceQuietPerAccount = 0,
            EventPriceAnnounced = 3000,
            EventPriceAnnouncedPerAccount = 0,
        };

        private const uint AvatarId = 7;
        private const uint Monarch = 1000;
        private const uint MonarchAcct = 100;
        private const uint VassalGuid = 1001;
        private const uint VassalAcct = 101;

        private static uint nextGuid = 0x7ECA0000;

        /// <summary>The tuning the fixture runs under; a scaled-price test swaps it before InitActive.</summary>
        private AvatarTuning tuning = T;

        private Store store;
        private AccountActivityIndex activity;
        private List<AvatarRoster> rosters;
        private List<WorldEventRequest> started;
        private List<Action> worldQueue;
        private List<(Player To, string Text)> told;
        private List<(uint Monarch, string Text)> allegiance;
        private int resent;
        private bool currentRequest;
        private (bool ok, string error) startResult;
        private Player monarch;

        [TestInitialize]
        public void Setup()
        {
            AvatarTestChart.Install();
            SalvageRollerVendorTests.SeedVendorConstructible();

            store = new Store();
            activity = new AccountActivityIndex();
            rosters = new List<AvatarRoster>();
            started = new List<WorldEventRequest>();
            worldQueue = new List<Action>();
            told = new List<(Player, string)>();
            allegiance = new List<(uint, string)>();
            resent = 0;
            currentRequest = false;
            startResult = (true, null);

            AvatarRights.AllegianceFacts = p => (Monarch, false);
            AvatarVendor.Tuning = () => tuning;
            AvatarAnnouncer.Sink = (m, text) => allegiance.Add((m, text));
            AvatarEventPurchase.StartSeam = r => { started.Add(r); return startResult; };
            AvatarEventPurchase.WorldEnqueue = a => worldQueue.Add(() => ((ActionEventDelegate)a).Action());
            AvatarEventPurchase.CurrentRunning = () => false;
            AvatarEventPurchase.NextScheduledUtc = () => null;
            AvatarEventPurchase.UtcNow = () => Now;
            AvatarEventPurchase.InThreadRun = p => false;
            AvatarEventPurchase.ResendPanel = (npc, p) => resent++;
            AvatarEventPurchase.IsCurrentRequest = r => currentRequest;

            monarch = SalvageRollerVendorTests.MakePlayer(Monarch);
            monarch.Level = 50;
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarTestChart.Reset();
            AvatarEventPurchase.ResetSeams();
            AvatarVendor.ResetSeams();
            AvatarRights.ResetSeams();
            AvatarAnnouncer.Sink = null;
            AvatarManager.Initialize(new Store { LoadAllReturnsNull = true }, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
        }

        // ---- Check (pure) ---------------------------------------------------------------------------------------

        private static Position Outdoors(uint instance = 0, float z = 0f) =>
            new Position((0x0090u << 16) | 0x0011, 50f, 50f, z, 0f, 0f, 0f, 1f, instance);

        private static Position Indoor() => new Position((0x0090u << 16) | 0x0111, 50f, 50f, 0f, 0f, 0f, 0f, 1f, 0);

        private static AvatarEventRefusal Check(bool permitted = true, bool active = true, int level = 50, Position spot = null,
            bool thread = false, bool running = false, DateTime? next = null, ulong available = 5000, ulong price = 2000, bool daily = false) =>
            AvatarEventPurchase.Check(permitted, active, level, daily, spot ?? Outdoors(), thread, running, next, Now, available, price, T);

        [TestMethod]
        public void Check_AllClear_None() => Assert.AreEqual(AvatarEventRefusal.None, Check());

        [TestMethod]
        public void Check_Indoors_Refused() => Assert.AreEqual(AvatarEventRefusal.Indoors, Check(spot: Indoor()));

        [TestMethod]
        public void Check_SlotBusy_Refused() => Assert.AreEqual(AvatarEventRefusal.SlotBusy, Check(running: true));

        [TestMethod]
        public void Check_ScheduledIn14Minutes_Refused() => Assert.AreEqual(AvatarEventRefusal.ScheduleSoon, Check(next: Now.AddMinutes(14)));

        [TestMethod]
        public void Check_ScheduledIn16Minutes_Allowed() => Assert.AreEqual(AvatarEventRefusal.None, Check(next: Now.AddMinutes(16)));

        [TestMethod]
        public void Check_ScheduledAtExactlyTheBuffer_Refused() =>
            Assert.AreEqual(AvatarEventRefusal.ScheduleSoon, Check(next: Now.AddMinutes(T.EventScheduleBufferMinutes)));

        [TestMethod]
        public void Check_AutoSchedulerOff_Allowed() => Assert.AreEqual(AvatarEventRefusal.None, Check(next: null));

        [TestMethod]
        public void Check_InsufficientFavor_Refused() => Assert.AreEqual(AvatarEventRefusal.InsufficientFavor, Check(available: 1999, price: 2000));

        [TestMethod]
        public void Check_ThreadRun_Level_Permission_Inactive_Refused()
        {
            Assert.AreEqual(AvatarEventRefusal.InThreadRun, Check(thread: true));
            Assert.AreEqual(AvatarEventRefusal.LevelTooLow, Check(level: 0));
            Assert.AreEqual(AvatarEventRefusal.NotPermitted, Check(permitted: false));
            Assert.AreEqual(AvatarEventRefusal.Inactive, Check(active: false));
        }

        [TestMethod]
        public void Check_DailyLimit_RefusedAfterTheLevelGate()
        {
            Assert.AreEqual(AvatarEventRefusal.DailyLimit, Check(daily: true));
            Assert.AreEqual(AvatarEventRefusal.LevelTooLow, Check(daily: true, level: 0), "the level gate is heard first");
            Assert.AreEqual(AvatarEventRefusal.DailyLimit, Check(daily: true, spot: Indoor(), running: true, available: 0),
                "the day's limit is heard before where, when and how much");
        }

        // ---- TryBuy ---------------------------------------------------------------------------------------------

        private AvatarState InitActive(ulong favor = 5000, ulong lifetime = 10_000, int r = 2, int s = 2)
        {
            store.Avatars.Add(new AllegianceAvatar
            {
                AvatarId = AvatarId, MonarchGuid = Monarch, State = (byte)AvatarBindState.Bound,
                Favor = favor, LifetimeXp = lifetime, LevelXp = lifetime, LastDecayDay = Today,
            });
            rosters.Add(AvatarScaleFixture.Roster(activity, r, s, Monarch, MonarchAcct, VassalGuid, VassalAcct, Start));

            AvatarManager.Initialize(store, () => tuning, () => Start, () => activity, () => rosters, m => rosters.FirstOrDefault(r => r.MonarchGuid == m));
            AvatarManager.Tick();

            var state = AvatarManager.Get(AvatarId);
            Assert.AreEqual(AvatarBindState.Bound, state.State);
            Assert.IsTrue(state.Active);
            return state;
        }

        private AvatarVendor MakeVendor()
        {
            var weenie = new Weenie
            {
                WeenieClassId = AvatarVendor.AvatarWcid,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.AllegianceAvatarVendor, true } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.MerchandiseItemTypes, 0 }, { PropertyInt.MerchandiseMinValue, 0 }, { PropertyInt.MerchandiseMaxValue, 0 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.BuyPrice, 0 }, { PropertyFloat.SellPrice, 1 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Allegiance Avatar" } },
            };

            var vendor = (AvatarVendor)WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(++nextGuid));
            vendor.AvatarId = AvatarId;
            vendor.Location = Outdoors(instance: 5, z: 12.5f);
            vendor.Told = (p, text) => told.Add((p, text));
            return vendor;
        }

        private static void Drain(List<Action> queue)
        {
            foreach (var a in queue.ToList())
                a();
            queue.Clear();
        }

        [TestMethod]
        public void TryBuy_StartReceivesNpcLocationAndAvatarId()
        {
            var state = InitActive();
            var npc = MakeVendor();

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line), line);
            Assert.AreEqual(0, started.Count, "the start runs on the world thread, after the current action");
            Assert.AreEqual(2000UL, state.ReservedFavor, "the price is held before the start");

            Drain(worldQueue);

            Assert.AreEqual(1, started.Count);
            var request = started[0];
            Assert.AreEqual(npc.Location.LandblockId.Raw, request.AnchorPosition.LandblockId.Raw);
            Assert.AreEqual(npc.Location.PositionX, request.AnchorPosition.PositionX);
            Assert.AreEqual(npc.Location.PositionY, request.AnchorPosition.PositionY);
            Assert.AreEqual(12.5f, request.AnchorPosition.PositionZ);
            Assert.AreEqual(5u, request.AnchorPosition.Instance);
            Assert.AreEqual(2000UL, request.AvatarFavorPrice, "the run carries the price it must settle");
            Assert.AreEqual((uint?)AvatarId, request.AvatarId);
            Assert.IsFalse(request.AnnounceServerWide, "the quiet listing keeps the announcements to the allegiance");
            Assert.IsTrue(request.Random);
            Assert.AreEqual($"avatar:{AvatarId}:{monarch.Name}", request.Invoker);
        }

        // ---- roster-scaled prices (owner ruling 2026-11-26) ----------------------------------------------------

        private static readonly AvatarTuning Scaled = AvatarTuning.Defaults with { EventMinLevel = 1 };

        [DataTestMethod]
        [DataRow(2, 230, false, 700UL)]
        [DataRow(2, 230, true, 1000UL)]
        [DataRow(20, 230, false, 4000UL)]
        [DataRow(20, 230, true, 6000UL)]
        [DataRow(73, 230, false, 10240UL)]
        [DataRow(73, 230, true, 15359UL)]
        public void TryBuy_ReservesTheScaledPrice_AndTheRunCarriesIt(int r, int s, bool announced, ulong price)
        {
            tuning = Scaled;
            var state = InitActive(favor: 40_000, lifetime: 10_000, r: r, s: s);
            Assert.AreEqual(r, state.ActiveAccounts, "the fixture reads R");
            Assert.AreEqual(s, AvatarManager.ServerActiveAccounts(), "the fixture reads S");
            var npc = MakeVendor();

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, announced, out var line), line);
            Assert.AreEqual(price, state.ReservedFavor, "the scaled price is what is held");

            Drain(worldQueue);

            Assert.AreEqual(price, started.Single().AvatarFavorPrice, "the run carries exactly the reserved price");

            // The reservation is never recomputed: the roster shrinking before the run settles changes nothing.
            state.ActiveAccounts = 2;
            AvatarEventPurchase.Settle(AvatarId, started.Single().AvatarFavorPrice, commit: true);
            Assert.AreEqual(40_000UL - price, state.Favor, "the commit debits the reserved price");
            Assert.AreEqual(0UL, state.ReservedFavor);
        }

        [TestMethod]
        public void TryBuy_ScaledPrice_RefusesWhenFavorIsUnderIt()
        {
            tuning = Scaled;
            var state = InitActive(favor: 3_999, r: 20, s: 230);
            var npc = MakeVendor();

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line), "the quiet price at R = 20 is 4000");
            Assert.IsNotNull(line);
            Assert.AreEqual(0UL, state.ReservedFavor, "a refusal holds nothing");
            Assert.AreEqual(0, worldQueue.Count);

            state.Favor = 4_000;
            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out _), "exactly the price passes");
            Assert.AreEqual(4_000UL, state.ReservedFavor);
        }

        [TestMethod]
        public void TryBuy_Announced_RequestIsServerWide()
        {
            InitActive();
            var npc = MakeVendor();

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, true, out _));
            Drain(worldQueue);

            Assert.IsTrue(started[0].AnnounceServerWide);
        }

        [TestMethod]
        public void SuccessfulStart_AnnouncesToAllegiance_KeepsFavorReserved_AndPanelShowsTheFigure()
        {
            var state = InitActive();
            var npc = MakeVendor();

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out _));
            Assert.AreEqual(1, resent, "the panel is re-sent as soon as the price is held");
            Assert.AreEqual(3000L, npc.AlternateCurrencyBalanceFor(monarch, AvatarVendor.FavorWcid), "available = Favor - Reserved");

            Drain(worldQueue);

            Assert.AreEqual(5000UL, state.Favor, "nothing is spent until the run reaches Active");
            Assert.AreEqual(2000UL, state.ReservedFavor, "the price stays held for the run to settle");
            Assert.AreEqual(3000L, npc.AlternateCurrencyBalanceFor(monarch, AvatarVendor.FavorWcid));
            Assert.AreEqual(1, allegiance.Count);
            Assert.AreEqual(Monarch, allegiance[0].Monarch);
            Assert.AreEqual($"{monarch.Name} has called a private World Event at {started[0].AnchorLabel}.", allegiance[0].Text);
            Assert.IsTrue(told.Any(t => t.To == monarch), "the buyer is told the outcome");

            AvatarEventPurchase.Settle(AvatarId, 2000, commit: true);

            Assert.AreEqual(3000UL, state.Favor);
            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.AreEqual(3000L, npc.AlternateCurrencyBalanceFor(monarch, AvatarVendor.FavorWcid), "the commit leaves the displayed balance unchanged");
        }

        [TestMethod]
        public void Settle_Release_ReturnsTheReservation()
        {
            var state = InitActive();
            Assert.IsTrue(AvatarManager.TryReserveFavor(AvatarId, 2000));

            AvatarEventPurchase.Settle(AvatarId, 2000, commit: false);

            Assert.AreEqual(5000UL, state.Favor);
            Assert.AreEqual(0UL, state.ReservedFavor);
        }

        [TestMethod]
        public void Commit_AfterTheBindingEnded_NeverDebitsALaterBindingsFavor()
        {
            var state = InitActive();
            Assert.IsTrue(AvatarManager.TryReserveFavor(AvatarId, 2000));

            Assert.IsTrue(AvatarManager.TryRetire(AvatarId, null), "retiring ends the binding and drops the reservation");
            Assert.AreEqual(0UL, state.ReservedFavor);
            state.Favor = 4000;

            AvatarEventPurchase.Settle(AvatarId, 2000, commit: true);

            Assert.AreEqual(4000UL, state.Favor, "the lost reservation must not debit what a later binding earned");
        }

        [TestMethod]
        public void StartThrows_RunNotCurrent_RefundsReservation()
        {
            var state = InitActive();
            var npc = MakeVendor();
            AvatarEventPurchase.StartSeam = r => throw new InvalidOperationException("boom");

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out _));
            Drain(worldQueue);

            Assert.AreEqual(5000UL, state.Favor);
            Assert.AreEqual(0UL, state.ReservedFavor, "no run took hold, so the price comes back");
            Assert.AreEqual(0, allegiance.Count);
        }

        [TestMethod]
        public void StartThrows_RunAlreadyCurrent_ReservationLeftToTheRun()
        {
            var state = InitActive();
            var npc = MakeVendor();
            AvatarEventPurchase.StartSeam = r => throw new InvalidOperationException("boom after the run took hold");
            currentRequest = true;

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out _));
            Drain(worldQueue);

            Assert.AreEqual(2000UL, state.ReservedFavor, "the run settles its own Favor; releasing here would refund a live event");
        }

        [TestMethod]
        public void FailedStart_RefundsReservation()
        {
            var state = InitActive();
            var npc = MakeVendor();
            startResult = (false, "an event is already running (run 5, Active)");

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out _));
            Assert.AreEqual(2000UL, state.ReservedFavor);

            Drain(worldQueue);

            Assert.AreEqual(5000UL, state.Favor, "a failed start spends nothing");
            Assert.AreEqual(0UL, state.ReservedFavor, "the reservation is released");
            Assert.AreEqual(0, allegiance.Count, "nothing is announced for a start that never happened");
            Assert.IsTrue(told.Any(t => t.To == monarch && t.Text.Contains("already running") && t.Text.Contains("No Favor was spent")));
            Assert.AreEqual(2, resent, "the panel is re-sent again so the refunded balance shows");
        }

        [TestMethod]
        public void ConcurrentPurchases_OnlyOneStarts_LoserRefunded()
        {
            var state = InitActive();
            var npc = MakeVendor();
            // Two slots a day, so both buys clear the daily limit and the race is between the starts. With one slot the
            // second buy is refused by the first one's pending claim (TryBuy_SecondBuyWhileTheFirstIsInFlight_Refused).
            var two = T with { EventDailyLimit = 2 };
            AvatarVendor.Tuning = () => two;
            var calls = 0;
            AvatarEventPurchase.StartSeam = r =>
            {
                started.Add(r);
                return ++calls == 1 ? (true, null) : (false, "an event is already running (run 1, Active)");
            };

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out _));
            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out _), "both pass Check: nothing has started yet");
            Assert.AreEqual(4000UL, state.ReservedFavor);

            Drain(worldQueue);

            Assert.AreEqual(2, started.Count);
            Assert.AreEqual(2000UL, state.ReservedFavor, "the loser is refunded; the winner's price stays held for its run");
            AvatarEventPurchase.Settle(AvatarId, 2000, commit: true);
            Assert.AreEqual(3000UL, state.Favor, "start value minus exactly one price");
            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.AreEqual((1, 0), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, AvatarDay.BuildClock(two, Now)),
                "the loser's claim was given back with its Favor; only the winner used a slot");
        }

        [TestMethod]
        public void TryBuy_RequestBuildThrows_RefusedWithNothingClaimedOrReserved()
        {
            var state = InitActive();
            var npc = MakeVendor();
            AvatarEventPurchase.DescribeAnchor = _ => throw new InvalidOperationException("town index down");

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line));

            Assert.AreEqual(AvatarEventPurchase.LineFor(AvatarEventRefusal.StartFailed, T), line);
            Assert.AreEqual((0, 0), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, AvatarDay.BuildClock(T, Now)), "no slot claimed");
            Assert.AreEqual(0UL, state.ReservedFavor, "no Favor held");
            Assert.AreEqual(0, worldQueue.Count);

            AvatarEventPurchase.DescribeAnchor = WorldEventTownIndex.Describe;
            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out line), "control: the same purchase succeeds once Describe works: " + line);
        }

        [TestMethod]
        public void TryBuy_EnqueueThrows_ReleasesTheClaimAndTheReservation()
        {
            var state = InitActive();
            var npc = MakeVendor();
            AvatarEventPurchase.WorldEnqueue = _ => throw new InvalidOperationException("queue gone");

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line));

            Assert.AreEqual(AvatarEventPurchase.LineFor(AvatarEventRefusal.StartFailed, T), line);
            Assert.AreEqual((0, 0), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, AvatarDay.BuildClock(T, Now)));
            Assert.AreEqual(0UL, state.ReservedFavor);
        }

        [TestMethod]
        public void TryBuy_NotActive_RefusedBeforeAnyFavorMoves()
        {
            var state = InitActive();
            var npc = MakeVendor();
            state.Active = false;

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line));

            Assert.AreEqual(AvatarEventPurchase.LineFor(AvatarEventRefusal.Inactive, T), line);
            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.AreEqual(0, worldQueue.Count);
        }

        [TestMethod]
        public void TryBuy_NotBound_RefusedBeforeAnyFavorMoves()
        {
            var state = InitActive();
            var npc = MakeVendor();
            state.State = AvatarBindState.Absorbed;

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line));

            Assert.AreEqual(AvatarEventPurchase.LineFor(AvatarEventRefusal.Inactive, T), line);
            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.AreEqual(0, worldQueue.Count);
        }

        [TestMethod]
        public void TryBuy_ScheduleSoon_RefusedWithClearLine_NoReservation()
        {
            var state = InitActive();
            var npc = MakeVendor();
            AvatarEventPurchase.NextScheduledUtc = () => Now.AddMinutes(10);

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line));

            StringAssert.Contains(line, "15 minutes");
            Assert.IsTrue(line.All(c => c < 128), "buyer lines are ASCII");
            Assert.AreEqual(0UL, state.ReservedFavor);
        }

        [TestMethod]
        public void TryBuy_AvatarBelowEventLevel_Refused()
        {
            var state = InitActive(lifetime: 0);
            var npc = MakeVendor();
            var strict = T with { EventMinLevel = 3 };
            AvatarVendor.Tuning = () => strict;
            monarch.Level = 200;

            Assert.IsTrue(state.Level(strict) < 3, "the fixture Avatar is below the gate");
            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line));

            Assert.AreEqual(AvatarEventPurchase.LineFor(AvatarEventRefusal.LevelTooLow, strict), line);
            StringAssert.Contains(line, "The Avatar must reach level 3");
            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.AreEqual(0, worldQueue.Count);
        }

        [TestMethod]
        public void TryBuy_SwitchOff_RefusedBeforeAnyReserve()
        {
            var state = InitActive();
            var npc = MakeVendor();
            var off = T with { Enabled = false };
            AvatarVendor.Tuning = () => off;

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line));

            Assert.AreEqual("The Allegiance Avatar is switched off.", line);
            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.AreEqual(0, worldQueue.Count);
            Assert.AreEqual(0, started.Count);
        }

        [TestMethod]
        public void TryBuy_EventAlreadyRunning_Refused()
        {
            var state = InitActive();
            var npc = MakeVendor();
            AvatarEventPurchase.CurrentRunning = () => true;

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line));

            Assert.AreEqual(AvatarEventPurchase.LineFor(AvatarEventRefusal.SlotBusy, T), line);
            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.AreEqual(0, worldQueue.Count);
        }

        [TestMethod]
        public void TryBuy_BuyerInThreadRun_Refused()
        {
            var state = InitActive();
            var npc = MakeVendor();
            AvatarEventPurchase.InThreadRun = p => p == monarch;

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line));

            Assert.AreEqual(AvatarEventPurchase.LineFor(AvatarEventRefusal.InThreadRun, T), line);
            Assert.AreEqual(0UL, state.ReservedFavor);
        }

        [TestMethod]
        public void TryBuy_NpcIndoors_Refused()
        {
            var state = InitActive();
            var npc = MakeVendor();
            npc.Location = Indoor();

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line));

            Assert.AreEqual(AvatarEventPurchase.LineFor(AvatarEventRefusal.Indoors, T), line);
            Assert.AreEqual(0UL, state.ReservedFavor);
        }

        [TestMethod]
        public void TryBuy_InsufficientFavor_Refused()
        {
            InitActive(favor: 100);
            var npc = MakeVendor();

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line));

            Assert.AreEqual(AvatarEventPurchase.LineFor(AvatarEventRefusal.InsufficientFavor, T), line);
            Assert.AreEqual(0UL, AvatarManager.Get(AvatarId).ReservedFavor, "refused by Check, before any reservation was attempted");
            Assert.AreEqual(0, resent, "no panel re-send: nothing was reserved");
        }

        // ---- daily limit (owner ruling 2026-11-25: one private World Event per Avatar per Eastern day) ------------

        // Now is 2027-01-15 12:00Z = 7:00 AM EST; the Avatar day turns over at 2027-01-16 05:00Z, 17 hours away.
        private const string LimitLine = "Your Avatar has already called a private World Event today. It can call another in 17h 0m.";

        /// <summary>Buys, starts and lets the run reach Active (Settle commit), the whole successful path.</summary>
        private void BuyAndReachActive(AvatarVendor npc, bool announced = false)
        {
            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, announced, out var line), line);
            Drain(worldQueue);
            AvatarEventPurchase.Settle(AvatarId, (ulong)(announced ? T.EventPriceAnnounced : T.EventPriceQuiet), commit: true);
        }

        [TestMethod]
        public void TryBuy_SecondBuySameDay_RefusedWithTheResetCountdown_NothingReserved()
        {
            var state = InitActive(favor: 10_000);
            var npc = MakeVendor();
            BuyAndReachActive(npc);
            var favorAfterFirst = state.Favor;

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line));

            Assert.AreEqual(LimitLine, line);
            Assert.IsTrue(line.All(c => c < 128), "buyer lines are ASCII");
            Assert.AreEqual(0UL, state.ReservedFavor, "refused before any Favor moved");
            Assert.AreEqual(favorAfterFirst, state.Favor);
            Assert.AreEqual(0, worldQueue.Count, "nothing queued");
            Assert.AreEqual(1, started.Count, "only the first event started");
        }

        [TestMethod]
        public void TryBuy_QuietThenAnnounced_ShareTheLimit()
        {
            InitActive(favor: 10_000);
            var npc = MakeVendor();
            BuyAndReachActive(npc, announced: false);

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, true, out var line), "the announced listing shares the quiet one's slot");
            Assert.AreEqual(LimitLine, line);
        }

        [TestMethod]
        public void TryBuy_SecondBuyWhileTheFirstIsInFlight_Refused()
        {
            var state = InitActive(favor: 10_000);
            var npc = MakeVendor();

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out _));

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, true, out var line), "the first buy's claim holds the slot until its run settles");
            Assert.AreEqual(LimitLine, line);
            Assert.AreEqual(2000UL, state.ReservedFavor, "only the first price is held");
        }

        [TestMethod]
        public void FailedStart_DoesNotUseTheDay()
        {
            InitActive(favor: 10_000);
            var npc = MakeVendor();
            startResult = (false, "no anchor fits");

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out _));
            Drain(worldQueue);

            startResult = (true, null);
            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line), "a start that failed gives the slot back: " + line);
        }

        [TestMethod]
        public void StartThrows_RunNotCurrent_DoesNotUseTheDay()
        {
            InitActive(favor: 10_000);
            var npc = MakeVendor();
            AvatarEventPurchase.StartSeam = r => throw new InvalidOperationException("boom");

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out _));
            Drain(worldQueue);

            Assert.AreEqual((0, 0), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, AvatarDay.BuildClock(T, Now)), "the claim was released");
        }

        [TestMethod]
        public void StartThrows_RunAlreadyCurrent_ClaimLeftToTheRun()
        {
            InitActive(favor: 10_000);
            var npc = MakeVendor();
            AvatarEventPurchase.StartSeam = r => throw new InvalidOperationException("boom after the run took hold");
            currentRequest = true;

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out _));
            Drain(worldQueue);

            Assert.AreEqual((0, 1), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, AvatarDay.BuildClock(T, Now)), "the run settles its own claim");
        }

        [TestMethod]
        public void RunEndedBeforeActive_DoesNotUseTheDay()
        {
            InitActive(favor: 10_000);
            var npc = MakeVendor();

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out _));
            Drain(worldQueue);
            AvatarEventPurchase.Settle(AvatarId, 2000, commit: false);

            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line), "a run that never reached Active is free: " + line);
        }

        [TestMethod]
        public void ReachingActive_UsesTheDay_AndTheFlushedRowCarriesIt()
        {
            InitActive(favor: 10_000);
            var npc = MakeVendor();
            BuyAndReachActive(npc);

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out _));

            AvatarManager.OnShutdown();
            var row = store.Saved.Last(a => a.AvatarId == AvatarId);
            Assert.AreEqual((byte)1, row.EventsOnDay, "the flushed row shows one event today");
            Assert.AreEqual(Now, row.LastEventAt, "stamped at the instant the run reached Active");
        }

        [TestMethod]
        public void FailedReserve_GivesTheClaimBack()
        {
            var state = InitActive(favor: 10_000);
            var npc = MakeVendor();
            AvatarEventPurchase.ReserveFavor = (id, price) => false;

            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line));
            Assert.AreEqual(AvatarEventPurchase.LineFor(AvatarEventRefusal.InsufficientFavor, T), line);
            Assert.AreEqual((0, 0), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, AvatarDay.BuildClock(T, Now)), "no claim is left held");

            AvatarEventPurchase.ReserveFavor = AvatarManager.TryReserveFavor;
            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out line), line);
            Assert.AreEqual(2000UL, state.ReservedFavor);
        }

        [TestMethod]
        public void NextEasternDay_IsAllowedAgain_AtTheBoundary()
        {
            InitActive(favor: 10_000);
            var npc = MakeVendor();
            BuyAndReachActive(npc);

            AvatarEventPurchase.UtcNow = () => new DateTime(2027, 1, 16, 4, 59, 59, DateTimeKind.Utc);
            Assert.IsFalse(AvatarEventPurchase.TryBuy(monarch, npc, false, out var line), "11:59:59 PM Eastern is still the same day");
            Assert.AreEqual("Your Avatar has already called a private World Event today. It can call another in 1m.", line);

            AvatarEventPurchase.UtcNow = () => new DateTime(2027, 1, 16, 5, 0, 0, DateTimeKind.Utc);
            Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out line), "12:00 AM Eastern starts a new Avatar day: " + line);
        }

        [TestMethod]
        public void LimitZero_IsUnlimited()
        {
            InitActive(favor: 20_000);
            var npc = MakeVendor();
            var unlimited = T with { EventDailyLimit = 0 };
            AvatarVendor.Tuning = () => unlimited;

            for (var i = 0; i < 3; i++)
                BuyAndReachActive(npc);

            Assert.AreEqual(3, started.Count);
        }

        [TestMethod]
        public void StatusText_ShowsTheDaysEventCount_AndTheCountdownWhenUsed()
        {
            var state = InitActive(favor: 10_000);
            var npc = MakeVendor();
            AvatarStatusText.UtcNow = () => Now;
            try
            {
                Assert.IsTrue(AvatarStatusText.Lines(state, T, (ulong)T.FavorCap, id => "a").Contains("Private World Events today: 0 of 1 used."));

                // In flight: the claim is held, and the purchase refuses on it, so the line counts it too.
                Assert.IsTrue(AvatarEventPurchase.TryBuy(monarch, npc, false, out _));
                Assert.IsTrue(AvatarStatusText.Lines(state, T, (ulong)T.FavorCap, id => "a").Contains("Private World Events today: 1 of 1 used. The next can be called in 17h 0m."),
                    "a pending claim is shown as used");
                Drain(worldQueue);
                AvatarEventPurchase.Settle(AvatarId, 2000, commit: false);
                Assert.IsTrue(AvatarStatusText.Lines(state, T, (ulong)T.FavorCap, id => "a").Contains("Private World Events today: 0 of 1 used."), "released: back to 0");

                BuyAndReachActive(npc);
                var lines = AvatarStatusText.Lines(state, T, (ulong)T.FavorCap, id => "a");

                Assert.IsTrue(lines.Contains("Private World Events today: 1 of 1 used. The next can be called in 17h 0m."), string.Join(" | ", lines));
                StringAssert.StartsWith(lines.Last(), "Next unlock:", "the unlock line stays last");
                Assert.IsNull(AvatarStatusText.EventDailyLine(state, T with { EventDailyLimit = 0 }, 50), "no line without a limit");
                Assert.IsNull(AvatarStatusText.EventDailyLine(state, T with { EventMinLevel = 99 }, 50), "no line before events unlock");
            }
            finally
            {
                AvatarStatusText.UtcNow = () => DateTime.UtcNow;
            }
        }

        private sealed class Store : IAvatarStore
        {
            public readonly List<AllegianceAvatar> Avatars = new List<AllegianceAvatar>();
            public readonly List<AllegianceAvatarContribution> Contributions = new List<AllegianceAvatarContribution>();
            public bool LoadAllReturnsNull;

            public List<AllegianceAvatar> LoadAll() => LoadAllReturnsNull ? null : Avatars.ToList();
            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => Contributions.Where(c => c.Day >= dayUtc).ToList();
            public readonly List<AllegianceAvatar> Saved = new List<AllegianceAvatar>();

            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done)
            {
                Saved.AddRange(a);
                done(true);
            }

            public void Insert(AllegianceAvatar row, Action<bool, uint> done) => done(true, 0);
        }
    }
}
