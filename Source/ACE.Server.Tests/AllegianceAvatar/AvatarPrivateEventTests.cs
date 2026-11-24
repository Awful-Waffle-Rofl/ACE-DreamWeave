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
        private static readonly AvatarTuning T = AvatarTuning.Defaults with { EventMinLevel = 1 };

        private const uint AvatarId = 7;
        private const uint Monarch = 1000;
        private const uint MonarchAcct = 100;
        private const uint VassalGuid = 1001;
        private const uint VassalAcct = 101;

        private static uint nextGuid = 0x7ECA0000;

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
            AvatarVendor.Tuning = () => T;
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
            bool thread = false, bool running = false, DateTime? next = null, ulong available = 5000, ulong price = 2000) =>
            AvatarEventPurchase.Check(permitted, active, level, spot ?? Outdoors(), thread, running, next, Now, available, price, T);

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

        // ---- TryBuy ---------------------------------------------------------------------------------------------

        private AvatarState InitActive(ulong favor = 5000, ulong lifetime = 10_000)
        {
            store.Avatars.Add(new AllegianceAvatar
            {
                AvatarId = AvatarId, MonarchGuid = Monarch, State = (byte)AvatarBindState.Bound,
                Favor = favor, LifetimeXp = lifetime, LevelXp = lifetime, LastDecayDay = Today,
            });
            rosters.Add(new AvatarRoster(Monarch, MonarchAcct, false, new List<KeeperCandidate>
            {
                new KeeperCandidate(Monarch, MonarchAcct, 1, 0, 0),
                new KeeperCandidate(VassalGuid, VassalAcct, 1, 0, 1),
            }));
            activity.Touch(MonarchAcct, Start);
            activity.Touch(VassalAcct, Start);

            AvatarManager.Initialize(store, () => T, () => Start, () => activity, () => rosters, m => rosters.FirstOrDefault(r => r.MonarchGuid == m));
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

        private sealed class Store : IAvatarStore
        {
            public readonly List<AllegianceAvatar> Avatars = new List<AllegianceAvatar>();
            public readonly List<AllegianceAvatarContribution> Contributions = new List<AllegianceAvatarContribution>();
            public bool LoadAllReturnsNull;

            public List<AllegianceAvatar> LoadAll() => LoadAllReturnsNull ? null : Avatars.ToList();
            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => Contributions.Where(c => c.Day >= dayUtc).ToList();
            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done) => done(true);
            public void Insert(AllegianceAvatar row, Action<bool, uint> done) => done(true, 0);
        }
    }
}
