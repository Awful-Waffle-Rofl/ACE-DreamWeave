using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// The Avatar NPCs through the manager seam: summon, Seat, unseat and the lifecycle's despawn/revalidate calls. The NPCs
    /// are built through the factory from an in-memory weenie, the players are seeded (no session), and every collaborator
    /// that would reach the world is an AvatarNpcs or AvatarLookApplier seam restored in cleanup. The only PropertyManager key read is the vendor
    /// constructor's vendor_shop_uses_generator, seeded by SeedVendorConstructible and restored in cleanup.
    /// </summary>
    [TestClass]
    public class AvatarNpcsTests
    {
        private const double Start = 1_800_000_000.0; // 2027-01-15 08:00:00 UTC
        private const double Day = 86400.0;
        private static readonly DateTime Today = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        private static readonly AvatarTuning T = AvatarTuning.Defaults;

        private const uint AvatarId = 7;
        private const uint OtherAvatarId = 8;
        private const uint Monarch = 1000;
        private const uint MonarchAcct = 100;
        private const uint Vassal = 1001;
        private const uint VassalAcct = 101;
        private const uint Plain = 1002;
        private const uint PlainAcct = 102;
        private const uint OtherMonarch = 2000;
        private const uint OtherMonarchAcct = 200;
        private const uint OtherVassal = 2001;
        private const uint OtherVassalAcct = 201;

        private const uint HouseA = 0x70090123;
        private const uint HouseB = 0x70A10456;

        private const string ShopGeneratorKey = "vendor_shop_uses_generator";

        private static uint nextGuid = 0x7ED10000;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "positionCache")]
        private static extern ref Dictionary<PositionType, Position> PositionCache(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ephemeralPositions")]
        private static extern ref Dictionary<PositionType, Position> EphemeralPositions(WorldObject wo);

        private Store store;
        private double now;
        private AccountActivityIndex activity;
        private Dictionary<uint, AvatarRoster> rosters;
        private Dictionary<uint, (uint Monarch, bool Seneschal)> facts;
        private Dictionary<uint, uint?> houses;
        private Dictionary<uint, Player> online;
        private List<(uint Monarch, string Text)> announced;
        private List<(Player To, string Text)> told;
        private List<(AvatarVendor Npc, double Seconds)> expiries;
        private List<uint> avatarIdAtPlacement;
        private List<(Confirmation_Choice Choice, string Text)> popups;
        private List<Action> worldQueue;
        private bool runWorldInline;
        private AvatarTuning cur;
        private Player monarch, vassal, plain, otherMonarch;

        [TestInitialize]
        public void Setup()
        {
            AvatarTestChart.Install();
            SalvageRollerVendorTests.SeedVendorConstructible();

            store = new Store();
            now = Start;
            activity = new AccountActivityIndex();
            rosters = new Dictionary<uint, AvatarRoster>();
            houses = new Dictionary<uint, uint?>();
            announced = new List<(uint, string)>();
            told = new List<(Player, string)>();
            expiries = new List<(AvatarVendor, double)>();
            avatarIdAtPlacement = new List<uint>();
            popups = new List<(Confirmation_Choice, string)>();
            worldQueue = new List<Action>();
            runWorldInline = true;
            cur = T;

            facts = new Dictionary<uint, (uint, bool)>
            {
                { Monarch, (Monarch, false) },
                { Vassal, (Monarch, true) },
                { Plain, (Monarch, false) },
                { OtherMonarch, (OtherMonarch, false) },
            };

            AvatarRights.AllegianceFacts = p => facts[p.Guid.Full];
            AvatarAnnouncer.Sink = (m, text) => announced.Add((m, text));

            // Build dresses every NPC (AvatarLookApplier.Apply); keep that off the world database, the dats and PropertyManager.
            AvatarLookApplier.ResetSeams();
            AvatarLookApplier.DefaultWeenie = NpcWeenie;
            AvatarLookApplier.FaceGenerator = _ => { };
            AvatarLookApplier.ScaleFactor = () => 2.0;

            AvatarNpcs.ClearTracking();
            AvatarNpcs.NpcBuilder = MakeNpc;
            AvatarNpcs.SummonPlacer = (npc, summoner) =>
            {
                avatarIdAtPlacement.Add(npc.AvatarId);
                npc.Location = new Position(summoner.Location);
                return true;
            };
            AvatarNpcs.SummonerState = (Player p, out string refusal) => { refusal = null; return true; };
            AvatarNpcs.IsDenylisted = p => p.LandblockShort == 0x00A0;
            AvatarNpcs.ScheduleExpiry = (npc, seconds) => expiries.Add((npc, seconds));
            AvatarNpcs.HouseInstanceOf = g => houses.TryGetValue(g, out var h) ? h : null;
            AvatarNpcs.LoadedLandblockOf = p => null;
            AvatarNpcs.WorldEnqueue = work => { if (runWorldInline) work(); else worldQueue.Add(work); };
            AvatarNpcs.SendConfirmation = (p, c, text) => { popups.Add(((Confirmation_Choice)c, text)); return true; };
            AvatarNpcs.Tell = (p, text) => told.Add((p, text));
            AvatarNpcs.OnlinePlayer = g => online.TryGetValue(g, out var p) ? p : null;

            monarch = MakePlayer(Monarch);
            vassal = MakePlayer(Vassal);
            plain = MakePlayer(Plain);
            otherMonarch = MakePlayer(OtherMonarch);
            online = new Dictionary<uint, Player> { [Monarch] = monarch, [Vassal] = vassal, [Plain] = plain, [OtherMonarch] = otherMonarch };
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarTestChart.Reset();
            PropertyManager.ModifyBool(ShopGeneratorKey, DefaultPropertyManager.DefaultBooleanProperties[ShopGeneratorKey].Item);
            AvatarNpcs.ResetSeams();
            AvatarNpcs.ClearTracking();
            AvatarLookApplier.ResetSeams();
            AvatarRights.ResetSeams();
            AvatarLifecycle.ResetSeams();
            AvatarAnnouncer.Sink = null;
            AvatarManager.Initialize(new Store { LoadAllReturnsNull = true }, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
        }

        // ---- fixture --------------------------------------------------------------------------------------------

        private static Weenie NpcWeenie() => new Weenie
        {
            WeenieClassId = AvatarVendor.AvatarWcid,
            WeenieType = WeenieType.Creature,
            PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.AllegianceAvatarVendor, true } },
            PropertiesInt = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.MerchandiseItemTypes, 0 },
                { PropertyInt.MerchandiseMinValue, 0 },
                { PropertyInt.MerchandiseMaxValue, 0 },
            },
            PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.BuyPrice, 0 }, { PropertyFloat.SellPrice, 1 } },
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Allegiance Avatar" } },
        };

        /// <summary>A static-range guid, so Destroy never reaches the dynamic guid allocator.</summary>
        private static WorldObject MakeNpc() => WorldObjectFactory.CreateWorldObject(NpcWeenie(), new ObjectGuid(++nextGuid));

        private static Position Outdoors(ushort landblock = 0x0090, uint instance = 0) =>
            new Position(((uint)landblock << 16) | 0x0011, 50f, 50f, 0f, 0f, 0f, 0f, 1f, instance);

        private static Player MakePlayer(uint guid)
        {
            var p = SalvageRollerVendorTests.MakePlayer(guid);
            PositionCache(p) = new Dictionary<PositionType, Position>();
            EphemeralPositions(p) = new Dictionary<PositionType, Position>();
            p.Location = Outdoors();
            return p;
        }

        private void SeedAvatar(uint id, uint monarchGuid, ulong lifetime = 10_000, ulong favor = 5000)
        {
            store.Avatars.Add(new AllegianceAvatar
            {
                AvatarId = id, MonarchGuid = monarchGuid, State = (byte)AvatarBindState.Bound,
                LifetimeXp = lifetime, LevelXp = lifetime, Favor = favor, LastDecayDay = Today,
            });
        }

        private void Roster(uint monarchGuid, uint monarchAcct, params KeeperCandidate[] vassals)
        {
            var members = new List<KeeperCandidate> { new KeeperCandidate(monarchGuid, monarchAcct, 5, 0, 0) };
            members.AddRange(vassals);
            rosters[monarchGuid] = new AvatarRoster(monarchGuid, monarchAcct, false, members);
        }

        /// <summary>Avatar 7 bound to monarch 1000 (vassal 1001 a Seneschal, 1002 plain), all active; Avatar 8 bound to 2000 with one vassal.</summary>
        private AvatarState Init(double monarchIdleDays = 0)
        {
            SeedAvatar(AvatarId, Monarch);
            SeedAvatar(OtherAvatarId, OtherMonarch);
            Roster(Monarch, MonarchAcct,
                new KeeperCandidate(Vassal, VassalAcct, 3, 2, 1),
                new KeeperCandidate(Plain, PlainAcct, 1, 0, 2));
            Roster(OtherMonarch, OtherMonarchAcct, new KeeperCandidate(OtherVassal, OtherVassalAcct, 1, 0, 1));

            activity.Touch(MonarchAcct, now - monarchIdleDays * Day);
            activity.Touch(VassalAcct, now);
            activity.Touch(PlainAcct, now);
            activity.Touch(OtherMonarchAcct, now);
            activity.Touch(OtherVassalAcct, now);

            AvatarManager.Initialize(store, () => cur, () => now, () => activity, () => rosters.Values.ToList(),
                m => rosters.TryGetValue(m, out var r) ? r : null);
            AvatarManager.Tick();

            var a = AvatarManager.Get(AvatarId);
            Assert.IsNotNull(a);
            Assert.AreEqual(AvatarBindState.Bound, a.State);
            Assert.IsTrue(a.Active, "the fixture must start Active");
            announced.Clear();
            return a;
        }

        private static AvatarNpcs.SeatSite Site(uint house, uint? owner, bool onProperty = true, HouseType type = HouseType.Mansion, Position at = null) =>
            new AvatarNpcs.SeatSite(house, type, owner, onProperty, at ?? Outdoors());

        /// <summary>Seats <paramref name="a"/> at a mansion its monarch owns and stands its NPC up on a fake landblock.</summary>
        private AvatarVendor SeatAndSpawn(AvatarState a, uint keeperGuid, uint house, ushort landblock = 0x0090)
        {
            houses[keeperGuid] = house;
            AvatarNpcs.SeatSiteOf = p => Site(house, keeperGuid, at: Outdoors(landblock));
            Assert.IsTrue(AvatarNpcs.TrySeat(online[keeperGuid], a, out var refusal), refusal);

            var added = new List<AvatarVendor>();
            AvatarNpcs.SpawnSeated(landblock, 0, npc => { added.Add(npc); return true; });
            var npcFor = added.Single(n => n.AvatarId == a.AvatarId);
            Assert.AreSame(npcFor, AvatarNpcs.SeatedNpc(a.AvatarId));
            return npcFor;
        }

        private AllegianceAvatar Flush(uint avatarId)
        {
            store.Saved.Clear();
            now += T.FlushSeconds;
            AvatarManager.Tick();
            return store.Saved.LastOrDefault(r => r.AvatarId == avatarId);
        }

        // ---- summon -------------------------------------------------------------------------------------------

        [TestMethod]
        public void Summon_TwiceDestroysFirst()
        {
            var a = Init();

            Assert.IsTrue(AvatarNpcs.TrySummon(vassal, a, out var r1), r1);
            var first = AvatarNpcs.SummonedNpc(AvatarId);
            Assert.IsNotNull(first);

            Assert.IsTrue(AvatarNpcs.TrySummon(monarch, a, out var r2), r2);
            var second = AvatarNpcs.SummonedNpc(AvatarId);

            Assert.AreNotSame(first, second);
            Assert.IsFalse(first.IsSeated || second.IsSeated, "a summoned NPC is the vendor, never marked seated");
            Assert.IsTrue(first.IsDestroyed, "the first summoned NPC is destroyed by the second summon");
            Assert.IsFalse(second.IsDestroyed);
            CollectionAssert.AreEqual(new[] { first, second }, expiries.Select(e => e.Npc).ToArray());
            Assert.IsTrue(expiries.All(e => e.Seconds == T.SummonSeconds), "each summon is timed for summonSeconds");
        }

        [TestMethod]
        public void CreatedNpc_CarriesTheAvatarId_BeforeItEntersTheWorld()
        {
            var a = Init();
            var b = AvatarManager.Get(OtherAvatarId);

            Assert.IsTrue(AvatarNpcs.TrySummon(vassal, a, out var r1), r1);
            Assert.IsTrue(AvatarNpcs.TrySummon(otherMonarch, b, out var r2), r2);
            CollectionAssert.AreEqual(new[] { AvatarId, OtherAvatarId }, avatarIdAtPlacement, "the summoned NPC carries its Avatar's id when it is placed");

            houses[Monarch] = HouseA;
            AvatarNpcs.SeatSiteOf = p => Site(HouseA, Monarch);
            Assert.IsTrue(AvatarNpcs.TrySeat(monarch, a, out var r3), r3);

            var idsAtAdd = new List<uint>();
            Assert.AreEqual(1, AvatarNpcs.SpawnSeated(0x0090, 0, npc => { idsAtAdd.Add(npc.AvatarId); return true; }));
            CollectionAssert.AreEqual(new[] { AvatarId }, idsAtAdd, "the seated NPC carries its Avatar's id when it is added to the landblock");
        }

        [TestMethod]
        public void SeatedAndSummonedNpcs_NeverRot()
        {
            var a = Init();

            Assert.IsTrue(AvatarNpcs.TrySummon(vassal, a, out var r1), r1);
            var summoned = AvatarNpcs.SummonedNpc(AvatarId);
            Assert.AreEqual(-1, summoned.TimeToRot, "the summoned NPC never rots; its summon timer removes it");

            houses[Monarch] = HouseA;
            AvatarNpcs.SeatSiteOf = p => Site(HouseA, Monarch);
            Assert.IsTrue(AvatarNpcs.TrySeat(monarch, a, out var r2), r2);

            AvatarVendor seated = null;
            Assert.AreEqual(1, AvatarNpcs.SpawnSeated(0x0090, 0, npc => { seated = npc; return true; }));
            Assert.AreEqual(-1, seated.TimeToRot, "the seated NPC never rots, or the landblock destroys it after 300 s");
        }

        [TestMethod]
        public void Summon_RefusedWhileTheAvatarFightsInItsEvent_BuildsNothing()
        {
            var a = Init();
            var b = AvatarManager.Get(OtherAvatarId);
            var built = 0;
            AvatarNpcs.NpcBuilder = () => { built++; return MakeNpc(); };
            AvatarNpcs.EventFighterUp = id => id == AvatarId;

            Assert.IsFalse(AvatarNpcs.TrySummon(vassal, a, out var refusal));
            Assert.AreEqual(ACE.Server.Avatars.Gauntlet.AvatarEventFighterHost.SummonRefusal, refusal);
            Assert.AreEqual(0, built, "nothing is built while the fighter is up");

            Assert.IsTrue(AvatarNpcs.TrySummon(otherMonarch, b, out var r2), r2);
            Assert.AreEqual(1, built, "control: another Avatar still summons");
        }

        [TestMethod]
        public void Summon_RefusedForPlainMember_Indoors_AndDenylisted_BuildsNothing()
        {
            var a = Init();
            var built = 0;
            AvatarNpcs.NpcBuilder = () => { built++; return MakeNpc(); };

            Assert.IsFalse(AvatarNpcs.TrySummon(plain, a, out var refusal));
            Assert.AreEqual(AvatarSeatRules.SummonNotAllowedMessage, refusal);

            vassal.Location = new Position(0x00900105, 50f, 50f, 0f, 0f, 0f, 0f, 1f, 0);
            Assert.IsFalse(AvatarNpcs.TrySummon(vassal, a, out refusal));
            Assert.AreEqual("The Avatar can only be summoned outdoors.", refusal);

            vassal.Location = Outdoors(0x00A0);
            Assert.IsFalse(AvatarNpcs.TrySummon(vassal, a, out refusal));
            Assert.AreEqual(AvatarSeatRules.SummonDenylistedMessage, refusal);

            AvatarNpcs.SummonerState = (Player p, out string r) => { r = "You are too busy."; return false; };
            vassal.Location = Outdoors();
            Assert.IsFalse(AvatarNpcs.TrySummon(vassal, a, out refusal));
            Assert.AreEqual("You are too busy.", refusal);

            Assert.AreEqual(0, built);
            Assert.IsNull(AvatarNpcs.SummonedNpc(AvatarId));
        }

        [TestMethod]
        public void Summon_PlacementFails_DestroysTheNpc_AndKeepsTheOldOne()
        {
            var a = Init();
            Assert.IsTrue(AvatarNpcs.TrySummon(vassal, a, out _));
            var first = AvatarNpcs.SummonedNpc(AvatarId);

            AvatarVendor failed = null;
            AvatarNpcs.SummonPlacer = (npc, p) => { failed = npc; return false; };

            Assert.IsFalse(AvatarNpcs.TrySummon(vassal, a, out var refusal));
            Assert.AreEqual(AvatarSeatRules.SummonFailedMessage, refusal);
            Assert.IsTrue(failed.IsDestroyed);
            Assert.AreSame(first, AvatarNpcs.SummonedNpc(AvatarId));
            Assert.IsFalse(first.IsDestroyed);
        }

        [TestMethod]
        public void SummonExpiry_DestroysOnlyTheNpcStillInTheSlot()
        {
            var a = Init();
            Assert.IsTrue(AvatarNpcs.TrySummon(vassal, a, out _));
            Assert.IsTrue(AvatarNpcs.TrySummon(vassal, a, out _));
            var (first, _) = expiries[0];
            var (second, _) = expiries[1];

            AvatarNpcs.ExpireSummoned(first);
            Assert.AreSame(second, AvatarNpcs.SummonedNpc(AvatarId), "an old timer never clears the new NPC's slot");

            AvatarNpcs.ExpireSummoned(second);
            Assert.IsTrue(second.IsDestroyed);
            Assert.IsNull(AvatarNpcs.SummonedNpc(AvatarId));
        }

        [TestMethod]
        public void TryPlaceVendor_AcceptsAvatarVendor()
        {
            // Compile-time pin: both TryPlaceVendor overloads take a Vendor, so an AvatarVendor goes through the mule ladder.
            Func<AvatarVendor, Player, bool> plain = MuleSummonHandler.TryPlaceVendor;
            Func<AvatarVendor, Player, Func<Position, bool>, bool> ruled = MuleSummonHandler.TryPlaceVendor;
            Assert.IsNotNull(plain);
            Assert.IsNotNull(ruled);

            var npc = (AvatarVendor)MakeNpc();
            npc.AvatarId = AvatarId;
            var summonerAt = Outdoors();
            var tried = new List<Position>();

            // The Avatar's rung rule (outdoors, not denylisted) over the real ladder: the first two rungs land on a denied spot.
            var placed = MuleSummonHandler.TryPlace(npc, summonerAt, 3.0f, wo =>
            {
                tried.Add(new Position(wo.Location));
                var denied = tried.Count <= 2;
                return AvatarSeatRules.IsPlacementRungPermitted(wo.Location, p => denied);
            });

            Assert.IsTrue(placed);
            Assert.AreEqual(3, tried.Count, "the ladder moves on to the next rung when a rung is refused");
            Assert.IsNotNull(npc.Home, "Home is set before the placement attempt");
            Assert.AreEqual(AvatarId, npc.AvatarId);
        }

        // ---- the lifecycle contract ---------------------------------------------------------------------------

        [TestMethod]
        public void SetHeir_KeepsSummoned_SeatedRevalidated()
        {
            var a = Init();
            var seated = SeatAndSpawn(a, Monarch, HouseA);
            Assert.IsTrue(AvatarNpcs.TrySummon(vassal, a, out _));
            var summonedNpc = AvatarNpcs.SummonedNpc(AvatarId);

            var revalidated = new List<AvatarState>();
            var unseated = new List<AvatarState>();
            AvatarNpcs.RevalidateSeatSink = s => revalidated.Add(s);
            AvatarNpcs.UnseatSink = s => unseated.Add(s);

            // Naming an heir (no Keeper change: the monarch is active) only revalidates the Seat.
            Assert.IsTrue(AvatarLifecycle.SetHeir(a, Vassal));

            Assert.IsFalse(summonedNpc.IsDestroyed, "naming an heir never despawns the summoned NPC");
            Assert.AreSame(summonedNpc, AvatarNpcs.SummonedNpc(AvatarId));
            Assert.IsFalse(seated.IsDestroyed, "the seated NPC is never just removed");
            Assert.AreSame(seated, AvatarNpcs.SeatedNpc(AvatarId));
            Assert.AreEqual(1, revalidated.Count);
            Assert.AreEqual(0, unseated.Count, "the Seat still qualifies");
            Assert.IsNotNull(AvatarManager.Get(AvatarId).Seat);
        }

        [TestMethod]
        public void KeeperChange_SeatInTheOldKeepersMansion_Unseats()
        {
            // Monarch idle 31 days: the Seneschal acts as Keeper and seats the Avatar in their own mansion.
            var a = Init(monarchIdleDays: 31);
            Assert.AreEqual(Vassal, a.KeeperGuid, "fixture: the vassal acts as Keeper");
            var seated = SeatAndSpawn(a, Vassal, HouseA);
            announced.Clear();

            // The monarch comes back: Keeper rights return to them, and the vassal's mansion stops qualifying.
            AvatarLifecycle.OnLogin(MonarchAcct, Monarch);

            Assert.AreEqual(Monarch, a.KeeperGuid);
            Assert.IsNull(a.Seat, "the Seat no longer qualifies, so the Avatar is unseated");
            Assert.IsTrue(seated.IsDestroyed);
            Assert.IsTrue(announced.Any(x => x.Monarch == Monarch && x.Text.StartsWith("The Avatar has left ") && x.Text.EndsWith(" and wanders again.")),
                string.Join(" | ", announced.Select(x => x.Text)));
        }

        [TestMethod]
        public void Unseat_KeepsLevelFavorLedger()
        {
            store.Contributions.Add(new AllegianceAvatarContribution { AvatarId = AvatarId, AccountId = VassalAcct, Day = Today, RawXp = 400, Credited = 400 });
            var a = Init();
            var seated = SeatAndSpawn(a, Monarch, HouseA);
            var seatedRow = Flush(AvatarId);
            Assert.AreEqual(HouseA, seatedRow.SeatHouseGuid, "fixture: the Seat was persisted");

            var lifetime = a.LifetimeXp;
            var favor = a.Favor;
            var level = a.Level(T);
            var top = AvatarManager.TopContributors(AvatarId, 5).ToList();
            var seatedAt = a.SeatedAtUtc;
            var version = a.Version;
            Assert.AreEqual(1, top.Count, "fixture: one contributor");

            AvatarNpcs.Unseat(a, "test");

            Assert.IsNull(a.Seat);
            Assert.IsTrue(seated.IsDestroyed);
            Assert.IsNull(AvatarNpcs.SeatedNpc(AvatarId));
            Assert.AreEqual(lifetime, a.LifetimeXp);
            Assert.AreEqual(favor, a.Favor);
            Assert.AreEqual(level, a.Level(T));
            CollectionAssert.AreEqual(top, AvatarManager.TopContributors(AvatarId, 5).ToList());
            Assert.AreEqual(seatedAt, a.SeatedAtUtc, "the cooldown keeps running from the last seat");
            Assert.AreEqual(version + 1, a.Version, "an unseat is a binding change");
            Assert.AreEqual(1, announced.Count(x => x.Monarch == Monarch && x.Text.EndsWith(" and wanders again.")));

            var row = Flush(AvatarId);
            Assert.IsNotNull(row, "the unseat marks the row dirty");
            Assert.IsNull(row.SeatHouseGuid, "the store receives the cleared Seat");
            Assert.AreEqual(lifetime, row.LifetimeXp);
            Assert.AreEqual(favor, row.Favor);
        }

        [TestMethod]
        public void OnHouseLost_UnseatsMatchingAvatarOnly()
        {
            var a = Init();
            var b = AvatarManager.Get(OtherAvatarId);
            var seatedA = SeatAndSpawn(a, Monarch, HouseA, 0x0090);
            var seatedB = SeatAndSpawn(b, OtherMonarch, HouseB, 0x00A1);
            announced.Clear();

            runWorldInline = false;
            AvatarNpcs.OnHouseLost(HouseA);
            Assert.IsNotNull(a.Seat, "nothing changes until the world thread runs the work");
            Assert.AreEqual(1, worldQueue.Count);
            worldQueue.Single()();

            Assert.IsNull(a.Seat);
            Assert.IsTrue(seatedA.IsDestroyed);
            Assert.IsNotNull(b.Seat);
            Assert.AreEqual(HouseB, b.Seat.HouseGuid);
            Assert.IsFalse(seatedB.IsDestroyed);
            Assert.AreSame(seatedB, AvatarNpcs.SeatedNpc(OtherAvatarId));
            CollectionAssert.AreEqual(new[] { Monarch }, announced.Select(x => x.Monarch).ToArray());
        }

        [TestMethod]
        public void TenMinuteRecheck_UnseatsWhenTheOwnerNoLongerHoldsTheMansion()
        {
            var a = Init();
            var seated = SeatAndSpawn(a, Monarch, HouseA);

            // The monarch no longer owns the mansion (sold, deleted), with no eviction hook to say so.
            houses[Monarch] = null;

            now += 599;
            AvatarManager.Tick();
            Assert.IsNotNull(a.Seat, "inside the 10-minute window nothing re-checks");

            now += 1;
            AvatarManager.Tick();
            Assert.IsNull(a.Seat, "the 10-minute re-check unseats");
            Assert.IsTrue(seated.IsDestroyed);
        }

        [TestMethod]
        public void AbsorbedAvatar_IsUnseatedByRevalidate()
        {
            var a = Init();
            SeatAndSpawn(a, Monarch, HouseA);

            Assert.IsTrue(AvatarManager.TryAbsorb(AvatarId, OtherAvatarId, null, null).HasValue);
            AvatarNpcs.RevalidateSeat(a);

            Assert.AreEqual(AvatarBindState.Absorbed, a.State);
            Assert.IsNull(a.Seat, "an Avatar that is no longer Bound loses its Seat");
            Assert.IsNull(AvatarNpcs.SeatedNpc(AvatarId));
        }

        // ---- live level -----------------------------------------------------------------------------------------

        [TestMethod]
        public void Summon_BuildsTheNpcAtTheAvatarsDerivedLevel()
        {
            var a = Init(); // lifetime 10,000 at the default tuning is level 4
            Assert.AreEqual(4, a.Level(T));

            Assert.IsTrue(AvatarNpcs.TrySummon(monarch, a, out var refusal), refusal);

            Assert.AreEqual(4, AvatarNpcs.SummonedNpc(AvatarId).Level, "the weenie's fixed 7 is replaced by the Avatar's level");
        }

        [TestMethod]
        public void Summon_LevelZeroAvatar_IsClampedToOne()
        {
            var a = Init();
            a.LevelXp = 0;

            Assert.IsTrue(AvatarNpcs.TrySummon(monarch, a, out var refusal), refusal);

            Assert.AreEqual(1, AvatarNpcs.SummonedNpc(AvatarId).Level);
        }

        [TestMethod]
        public void Summon_LevelUpBetweenBuildAndRegistration_IsResyncedOntoTheNpc()
        {
            var a = Init(); // level 4 at build time
            AvatarLevelUp.ApplyToNpc = AvatarLevelUp.ApplyLevel; // inline stand-in for the NPC's own queue

            // The placer runs after Build and before the NPC is registered: a flush lands here and lifts the Avatar
            // to level 5 (15,810), and its dispatch finds no NPC to update.
            AvatarNpcs.SummonPlacer = (npc, summoner) =>
            {
                npc.Location = new Position(summoner.Location);
                a.LevelXp = 16_000;
                return true;
            };

            try
            {
                Assert.IsTrue(AvatarNpcs.TrySummon(monarch, a, out var refusal), refusal);
            }
            finally
            {
                AvatarLevelUp.ResetSeams();
            }

            Assert.AreEqual(5, AvatarNpcs.SummonedNpc(AvatarId).Level);
        }

        [TestMethod]
        public void LevelUp_ApplyLevel_SetsTheLevel_AndSkipsADestroyedNpc()
        {
            var npc = (AvatarVendor)MakeNpc();

            // EnqueueBroadcast and PlayParticleEffect on a detached NPC (no landblock) are no-ops in this harness, so
            // only the Level write is observable here; the wire messages are not asserted.
            AvatarLevelUp.ApplyLevel(npc, 9);
            Assert.AreEqual(9, npc.Level);

            npc.Destroy();
            AvatarLevelUp.ApplyLevel(npc, 12);
            Assert.AreEqual(9, npc.Level, "a destroyed NPC is left alone");
        }

        [TestMethod]
        public void LevelUp_Handle_AppliesTheNewLevelToBothLiveNpcs()
        {
            var a = Init();
            var seated = SeatAndSpawn(a, Monarch, HouseA);
            Assert.IsTrue(AvatarNpcs.TrySummon(monarch, a, out var refusal), refusal);
            var summoned = AvatarNpcs.SummonedNpc(AvatarId);
            var applied = new List<(AvatarVendor Npc, int Level)>();
            AvatarLevelUp.ApplyToNpc = (npc, level) => applied.Add((npc, level));

            try
            {
                AvatarLevelUp.Handle(new AvatarLevelUpEvent(AvatarId, Monarch, 4, 5), T);
            }
            finally
            {
                AvatarLevelUp.ResetSeams();
            }

            Assert.AreEqual(2, applied.Count);
            CollectionAssert.AreEquivalent(new[] { seated, summoned }, applied.Select(x => x.Npc).ToArray());
            Assert.IsTrue(applied.All(x => x.Level == 5));
        }

        // ---- seat -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Seat_PopupYes_StoresSeatAndStamps_OnTheWorldThread()
        {
            var a = Init();
            houses[Monarch] = HouseA;
            var at = Outdoors();
            AvatarNpcs.SeatSiteOf = p => Site(HouseA, Monarch, at: at);
            var version = a.Version;

            runWorldInline = false;
            AvatarNpcs.RequestSeat(monarch, a);

            Assert.AreEqual(1, popups.Count);
            Assert.AreEqual("Seat the Avatar here?", popups[0].Text);
            Assert.IsNull(a.Seat, "nothing is written before the answer");

            popups[0].Choice.Dispatch(true, false, DateTime.UtcNow);
            Assert.IsNull(a.Seat, "Yes hands the write to the world thread");
            worldQueue.Single()();

            Assert.IsNotNull(a.Seat);
            Assert.AreEqual(HouseA, a.Seat.HouseGuid);
            Assert.AreEqual(at.Cell, a.Seat.Position.Cell);
            Assert.AreNotSame(at, a.Seat.Position, "the Seat holds its own copy of the position");
            Assert.AreEqual(new DateTime(2027, 1, 15, 8, 0, 0, DateTimeKind.Utc), a.SeatedAtUtc);
            Assert.AreEqual(version + 1, a.Version);
            Assert.AreEqual(AvatarSeatRules.SeatedMessage, told.Last().Text);
        }

        [TestMethod]
        public void Seat_PopupYes_AfterAChange_IsCancelled()
        {
            var a = Init();
            houses[Monarch] = HouseA;
            AvatarNpcs.SeatSiteOf = p => Site(HouseA, Monarch);

            AvatarNpcs.RequestSeat(monarch, a);
            Assert.IsTrue(AvatarLifecycle.SetHeir(a, Vassal), "a binding change while the popup is open");

            popups.Single().Choice.Dispatch(true, false, DateTime.UtcNow);

            Assert.IsNull(a.Seat);
            Assert.AreEqual("The Avatar was not seated: the Avatar has changed since you were asked.", told.Last().Text);
        }

        [TestMethod]
        public void Seat_PopupYes_AfterSwitchOff_IsRefused()
        {
            var a = Init();
            houses[Monarch] = HouseA;
            AvatarNpcs.SeatSiteOf = p => Site(HouseA, Monarch);

            runWorldInline = false;
            AvatarNpcs.RequestSeat(monarch, a);
            cur = T with { Enabled = false };

            popups.Single().Choice.Dispatch(true, false, DateTime.UtcNow);
            worldQueue.Single()();

            Assert.IsNull(a.Seat);
            Assert.AreEqual(AvatarSeatRules.SwitchedOffMessage, told.Last().Text);
        }

        [TestMethod]
        public void SpawnSeated_AfterTheLandblockUnloaded_SpawnsAgain()
        {
            var a = Init();
            var first = SeatAndSpawn(a, Monarch, HouseA);

            // A landblock unload destroys its objects; the tracking entry still names the destroyed NPC.
            first.Destroy();
            Assert.IsTrue(first.IsDestroyed);

            var added = new List<AvatarVendor>();
            var count = AvatarNpcs.SpawnSeated(0x0090, 0, npc => { added.Add(npc); return true; });

            Assert.AreEqual(1, count, "the reloaded landblock gets its seated NPC back");
            var again = added.Single(n => n.AvatarId == a.AvatarId);
            Assert.AreNotSame(first, again);
            Assert.AreSame(again, AvatarNpcs.SeatedNpc(a.AvatarId));
        }

        [TestMethod]
        public void Seat_PopupNo_WritesNothing()
        {
            var a = Init();
            AvatarNpcs.SeatSiteOf = p => Site(HouseA, Monarch);

            AvatarNpcs.RequestSeat(monarch, a);
            popups.Single().Choice.Dispatch(false, false, DateTime.UtcNow);

            Assert.IsNull(a.Seat);
        }

        [TestMethod]
        public void Seat_Refusals()
        {
            var a = Init();
            cur = T with { ReseatCooldownDays = 7 };

            AvatarNpcs.SeatSiteOf = p => Site(HouseA, Monarch);
            Assert.IsFalse(AvatarNpcs.TrySeat(vassal, a, out var refusal));
            Assert.AreEqual(AvatarSeatRules.SeatNotKeeperMessage, refusal);

            AvatarNpcs.SeatSiteOf = p => null;
            Assert.IsFalse(AvatarNpcs.TrySeat(monarch, a, out refusal));
            Assert.AreEqual(AvatarSeatRules.SeatNoMansionMessage, refusal);

            AvatarNpcs.SeatSiteOf = p => Site(HouseA, Monarch, type: HouseType.Villa);
            Assert.IsFalse(AvatarNpcs.TrySeat(monarch, a, out refusal));
            Assert.AreEqual(AvatarSeatRules.SeatNoMansionMessage, refusal);

            AvatarNpcs.SeatSiteOf = p => Site(HouseA, Monarch, onProperty: false);
            Assert.IsFalse(AvatarNpcs.TrySeat(monarch, a, out refusal));
            Assert.AreEqual(AvatarSeatRules.SeatNotOnPropertyMessage, refusal);

            AvatarNpcs.SeatSiteOf = p => Site(HouseA, Plain);
            Assert.IsFalse(AvatarNpcs.TrySeat(monarch, a, out refusal));
            Assert.AreEqual(AvatarSeatRules.SeatNotQualifiedMessage, refusal);

            AvatarNpcs.SeatSiteOf = p => Site(HouseA, Monarch);
            Assert.IsTrue(AvatarNpcs.TrySeat(monarch, a, out refusal), refusal);

            now += 6 * Day;
            AvatarNpcs.SeatSiteOf = p => Site(HouseB, Monarch);
            Assert.IsFalse(AvatarNpcs.TrySeat(monarch, a, out refusal));
            Assert.AreEqual("The Avatar can't be moved again for 1 more day.", refusal);
            Assert.AreEqual(HouseA, a.Seat.HouseGuid);

            AvatarNpcs.RequestSeat(monarch, a);
            Assert.AreEqual(0, popups.Count, "a refused seat never asks");
            Assert.AreEqual("The Avatar can't be moved again for 1 more day.", told.Last().Text);
        }

        [TestMethod]
        public void SpawnSeated_OnlyThisLandblockAndInstance_AndOnlyOnce()
        {
            var a = Init();
            houses[Monarch] = HouseA;
            AvatarNpcs.SeatSiteOf = p => Site(HouseA, Monarch, at: Outdoors(0x0090, 0));
            Assert.IsTrue(AvatarNpcs.TrySeat(monarch, a, out _));

            Assert.AreEqual(0, AvatarNpcs.SpawnSeated(0x0091, 0, npc => true), "another landblock");
            Assert.AreEqual(0, AvatarNpcs.SpawnSeated(0x0090, 5, npc => true), "another instance");

            AvatarVendor placed = null;
            var seatedAtAdd = false;
            Assert.AreEqual(1, AvatarNpcs.SpawnSeated(0x0090, 0, npc => { placed = npc; seatedAtAdd = npc.IsSeated; return true; }));
            Assert.IsTrue(seatedAtAdd, "the seated NPC is marked seated before it enters the world, so it never trades");
            Assert.AreEqual(a.Seat.Position.Cell, placed.Location.Cell);
            Assert.AreEqual(a.Seat.Position.Cell, placed.Home.Cell);
            Assert.AreEqual(0, AvatarNpcs.SpawnSeated(0x0090, 0, npc => true), "an NPC already stands at this Seat");
        }

        [TestMethod]
        public void AvatarNpc_IsNeverPersisted()
        {
            var npc = WorldObjectFactory.CreateWorldObject(NpcWeenie(), new ObjectGuid(0x80FFFF01));

            Assert.IsInstanceOfType(npc, typeof(AvatarVendor));
            Assert.IsTrue(npc.Guid.IsDynamic(), "fixture: a dynamic guid, like the summon and seat spawns get");
            Assert.IsFalse(npc.IsDynamicThatShouldPersistToShard());
        }

        // ---- store fake -----------------------------------------------------------------------------------------

        private sealed class Store : IAvatarStore
        {
            public readonly List<AllegianceAvatar> Avatars = new List<AllegianceAvatar>();
            public readonly List<AllegianceAvatarContribution> Contributions = new List<AllegianceAvatarContribution>();
            public readonly List<AllegianceAvatar> Saved = new List<AllegianceAvatar>();
            public bool LoadAllReturnsNull;

            public List<AllegianceAvatar> LoadAll() => LoadAllReturnsNull ? null : Avatars.ToList();
            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => Contributions.Where(c => c.Day >= dayUtc).ToList();

            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done)
            {
                Saved.AddRange(a);
                done(true);
            }

            public void Insert(AllegianceAvatar row, Action<bool, uint> done) => done(true, 0);
        }
    }
}
