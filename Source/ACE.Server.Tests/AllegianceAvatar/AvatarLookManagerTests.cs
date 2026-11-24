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
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// The stored look through the manager and the NPC spawn paths: TrySetLook, the parsed look cached on load, the look a
    /// fresh NPC is built with, Redress replacing every live NPC, and the dress cooldown tunable. Every collaborator that
    /// would reach the world is an AvatarNpcs or AvatarLookApplier seam, restored in cleanup. The PropertyManager keys read
    /// are the vendor constructor's vendor_shop_uses_generator (seeded by SeedVendorConstructible) and, in
    /// Tuning_reads_dress_cooldown, every allegiance_avatar_* key; each is restored.
    /// </summary>
    [TestClass]
    public class AvatarLookManagerTests
    {
        private const double Start = 1_800_000_000.0; // 2027-01-15 08:00:00 UTC
        private static readonly DateTime Today = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        private static readonly AvatarTuning T = AvatarTuning.Defaults;

        private const uint AvatarId = 7;
        private const uint RetiredId = 9;
        private const uint CorruptId = 10;
        private const uint Monarch = 1000;
        private const uint MonarchAcct = 100;
        private const uint HouseA = 0x70090123;

        private const uint Jerkin = 124, Breeches = 117;
        private const EquipMask Chest = EquipMask.ChestArmor, Legs = EquipMask.LowerLegWear;

        private const string ShopGeneratorKey = "vendor_shop_uses_generator";
        private const string DressCooldownKey = "allegiance_avatar_dress_cooldown_seconds";

        private static uint nextGuid = 0x7ED20000;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "positionCache")]
        private static extern ref Dictionary<PositionType, Position> PositionCache(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ephemeralPositions")]
        private static extern ref Dictionary<PositionType, Position> EphemeralPositions(WorldObject wo);

        private Store store;
        private double now;
        private AccountActivityIndex activity;
        private Dictionary<uint, AvatarRoster> rosters;
        private List<(AvatarVendor Npc, double Seconds)> expiries;
        private List<Action> worldQueue;
        private List<Action> landblockQueue;
        private List<(AvatarVendor Npc, Position At)> replacedAt;
        private List<AvatarVendor> addedToLandblock;
        private int fallbackCalls;
        private Player monarch;

        [TestInitialize]
        public void Setup()
        {
            AvatarTestChart.Install();
            SalvageRollerVendorTests.SeedVendorConstructible();

            store = new Store();
            now = Start;
            activity = new AccountActivityIndex();
            rosters = new Dictionary<uint, AvatarRoster>();
            expiries = new List<(AvatarVendor, double)>();
            worldQueue = new List<Action>();
            landblockQueue = new List<Action>();
            replacedAt = new List<(AvatarVendor, Position)>();
            addedToLandblock = new List<AvatarVendor>();
            fallbackCalls = 0;

            AvatarRights.AllegianceFacts = p => (Monarch, false);
            AvatarAnnouncer.Sink = (m, text) => { };

            AvatarLookApplier.ResetSeams();
            AvatarLookApplier.PieceBuilder = BuildPiece;
            AvatarLookApplier.HeritageGenderValid = (h, g) => true;
            AvatarLookApplier.DefaultWeenie = () => NpcWeenie();
            AvatarLookApplier.FaceGenerator = _ => { };
            AvatarLookApplier.ScaleFactor = () => 2.0;

            AvatarNpcs.ClearTracking();
            AvatarNpcs.NpcBuilder = MakeNpc;
            AvatarNpcs.SummonPlacer = (npc, summoner) => { npc.Location = new Position(summoner.Location); return true; };
            AvatarNpcs.SummonerState = (Player p, out string refusal) => { refusal = null; return true; };
            AvatarNpcs.IsDenylisted = p => false;
            AvatarNpcs.ScheduleExpiry = (npc, seconds) => expiries.Add((npc, seconds));
            AvatarNpcs.LoadedLandblockOf = p => null;
            AvatarNpcs.HouseInstanceOf = g => g == Monarch ? HouseA : null; // the monarch owns the Seat's mansion, so a re-check keeps it
            AvatarNpcs.WorldEnqueue = work => worldQueue.Add(work);
            AvatarNpcs.SummonReplacer = (npc, at) => { npc.Location = new Position(at); replacedAt.Add((npc, at)); return true; };
            // Redress swallows seam exceptions, so the default records the call instead of asserting inside the seam.
            AvatarNpcs.SummonFallbackPlacer = (npc, around) => { fallbackCalls++; return false; };
            AvatarNpcs.SeatLandblockEnqueue = (p, work) =>
            {
                landblockQueue.Add(() => work(npc => { addedToLandblock.Add(npc); return true; }));
                return true;
            };

            monarch = SalvageRollerVendorTests.MakePlayer(Monarch);
            PositionCache(monarch) = new Dictionary<PositionType, Position>();
            EphemeralPositions(monarch) = new Dictionary<PositionType, Position>();
            monarch.Location = Outdoors();
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarTestChart.Reset();
            PropertyManager.ModifyBool(ShopGeneratorKey, DefaultPropertyManager.DefaultBooleanProperties[ShopGeneratorKey].Item);
            AvatarNpcs.ResetSeams();
            AvatarNpcs.ClearTracking();
            AvatarLookApplier.ResetSeams();
            AvatarLevelUp.ResetSeams();
            AvatarRights.ResetSeams();
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

        private static WorldObject BuildPiece(uint wcid)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ValidLocations, wcid == Jerkin ? (int)Chest : (int)Legs } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Piece " + wcid } },
            };
            return new GenericObject(weenie, new ObjectGuid(++nextGuid));
        }

        private static AvatarLookPiece Piece(uint wcid, EquipMask slot) =>
            new AvatarLookPiece(wcid, slot,
                new Dictionary<PropertyInt, int> { { PropertyInt.PaletteTemplate, 85 } },
                new Dictionary<PropertyFloat, double>(),
                new Dictionary<PropertyDataId, uint> { { PropertyDataId.ClothingBase, 0x10000ABC } },
                new Dictionary<PropertyBool, bool>(),
                new List<AvatarLookObjDescRow>(),
                "Piece " + wcid,
                new Dictionary<PropertyString, string>());

        private static AvatarLook Look(params AvatarLookPiece[] pieces) =>
            new AvatarLook(AvatarLookJson.CurrentVersion, new DateTime(2027, 1, 15, 8, 0, 0, DateTimeKind.Utc),
                new AvatarLookBody(
                    new Dictionary<PropertyInt, int> { { PropertyInt.Gender, 2 }, { PropertyInt.HeritageGroup, 1 } },
                    new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, 1.1 } },
                    new Dictionary<PropertyDataId, uint> { { PropertyDataId.HeadObject, 0x01000A00 } },
                    new Dictionary<PropertyString, string> { { PropertyString.Sex, "Female" }, { PropertyString.HeritageGroup, "Aluvian" } },
                    null, null, MotionStance.NonCombat),
                pieces);

        private static string LookJson(params AvatarLookPiece[] pieces) => AvatarLookJson.Serialize(Look(pieces));

        private static Position Outdoors(ushort landblock = 0x0090, uint instance = 0) =>
            new Position(((uint)landblock << 16) | 0x0011, 50f, 50f, 0f, 0f, 0f, 0f, 1f, instance);

        private static AllegianceAvatar Row(uint id, uint? monarchGuid, AvatarBindState state = AvatarBindState.Bound, string lookJson = null, Position seat = null)
        {
            var row = new AllegianceAvatar
            {
                AvatarId = id, MonarchGuid = monarchGuid, State = (byte)state,
                LifetimeXp = 10_000, LevelXp = 10_000, Favor = 5000, LastDecayDay = Today, LookJson = lookJson,
            };

            if (seat != null)
            {
                row.SeatHouseGuid = HouseA;
                row.SeatCell = seat.Cell;
                row.SeatInstance = seat.Instance;
                row.SeatX = seat.PositionX;
                row.SeatY = seat.PositionY;
                row.SeatZ = seat.PositionZ;
                row.SeatQw = seat.RotationW;
                row.SeatQx = seat.RotationX;
                row.SeatQy = seat.RotationY;
                row.SeatQz = seat.RotationZ;
                row.SeatedAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }

            return row;
        }

        /// <summary>Loads the store's rows (Avatar 7 bound to monarch 1000, active) and settles the Keeper.</summary>
        private AvatarState Init()
        {
            rosters[Monarch] = new AvatarRoster(Monarch, MonarchAcct, false, new List<KeeperCandidate> { new KeeperCandidate(Monarch, MonarchAcct, 5, 0, 0) });
            activity.Touch(MonarchAcct, now);

            AvatarManager.Initialize(store, () => T, () => now, () => activity, () => rosters.Values.ToList(),
                m => rosters.TryGetValue(m, out var r) ? r : null);
            AvatarManager.Tick();

            var a = AvatarManager.Get(AvatarId);
            Assert.IsNotNull(a);
            Assert.AreEqual(AvatarBindState.Bound, a.State);
            return a;
        }

        private static uint[] Worn(AvatarVendor npc) => npc.EquippedObjects.Values.Select(i => i.WeenieClassId).OrderBy(w => w).ToArray();

        private void RunAll(List<Action> queue)
        {
            var work = queue.ToList();
            queue.Clear();
            foreach (var w in work)
                w();
        }

        // ---- TrySetLook -----------------------------------------------------------------------------------------

        [TestMethod]
        public void TrySetLook_bound_only_and_bumps_version()
        {
            store.Avatars.Add(Row(AvatarId, Monarch));
            store.Avatars.Add(Row(RetiredId, null, AvatarBindState.Retired));
            var a = Init();
            var before = a.Version;
            var json = LookJson(Piece(Jerkin, Chest));

            Assert.IsTrue(AvatarManager.TrySetLook(AvatarId, json, null));
            Assert.AreEqual(before + 1, a.Version, "a look is part of what the Avatar is, so Version bumps");
            Assert.AreEqual(json, a.LookJson);
            Assert.AreEqual(Look(Piece(Jerkin, Chest)), a.Look, "the parsed look is cached with the JSON");

            Assert.IsFalse(AvatarManager.TrySetLook(RetiredId, json, null), "a retired Avatar is never dressed");
            Assert.IsNull(AvatarManager.Get(RetiredId).LookJson);
            Assert.IsFalse(AvatarManager.TrySetLook(999, json, null), "no such Avatar");

            store.Saved.Clear();
            now += T.FlushSeconds;
            AvatarManager.Tick();
            Assert.AreEqual(json, store.Saved.Last(r => r.AvatarId == AvatarId).LookJson, "the row is dirty and the flush carries the look");

            Assert.IsTrue(AvatarManager.TrySetLook(AvatarId, null, a.Version), "null clears the look");
            Assert.IsNull(a.LookJson);
            Assert.IsNull(a.Look);
        }

        [TestMethod]
        public void TrySetLook_stale_version_refused()
        {
            store.Avatars.Add(Row(AvatarId, Monarch));
            var a = Init();
            var shown = a.Version;

            Assert.IsTrue(AvatarManager.TrySetHeir(AvatarId, 4242, null));
            Assert.AreNotEqual(shown, a.Version);

            Assert.IsFalse(AvatarManager.TrySetLook(AvatarId, LookJson(Piece(Jerkin, Chest)), shown), "the Avatar changed since the popup was shown");
            Assert.IsNull(a.LookJson);
            Assert.IsNull(a.Look);

            Assert.IsTrue(AvatarManager.TrySetLook(AvatarId, LookJson(Piece(Jerkin, Chest)), a.Version));
        }

        [TestMethod]
        public void TrySetLook_refuses_json_that_does_not_parse()
        {
            store.Avatars.Add(Row(AvatarId, Monarch));
            var a = Init();
            var before = a.Version;

            Assert.IsFalse(AvatarManager.TrySetLook(AvatarId, "{not json", null));
            Assert.IsFalse(AvatarManager.TrySetLook(AvatarId, new string('x', AvatarLookJson.MaxBytes + 1), null), "over the size cap");
            Assert.IsNull(a.LookJson, "LookJson and Look never disagree");
            Assert.AreEqual(before, a.Version);
        }

        [TestMethod]
        public void Look_parsed_on_load_and_null_on_corrupt()
        {
            var json = LookJson(Piece(Jerkin, Chest), Piece(Breeches, Legs));
            store.Avatars.Add(Row(AvatarId, Monarch, lookJson: json));
            store.Avatars.Add(Row(CorruptId, 3000, lookJson: "{\"Version\":1,\"Pieces\":"));
            Init();

            var a = AvatarManager.Get(AvatarId);
            Assert.AreEqual(json, a.LookJson);
            Assert.AreEqual(Look(Piece(Jerkin, Chest), Piece(Breeches, Legs)), a.Look);

            var corrupt = AvatarManager.Get(CorruptId);
            Assert.AreEqual("{\"Version\":1,\"Pieces\":", corrupt.LookJson, "the stored text is kept verbatim");
            Assert.IsNull(corrupt.Look, "a corrupt look reads as the default look");

            Assert.IsNull(ShardAvatarStore.FromEntity(Row(1, Monarch)).Look, "never dressed");
        }

        // ---- spawn ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Build_applies_stored_look()
        {
            var seat = Outdoors();
            store.Avatars.Add(Row(AvatarId, Monarch, seat: seat));
            Init();

            var added = new List<AvatarVendor>();
            Assert.AreEqual(1, AvatarNpcs.SpawnSeated(0x0090, 0, npc => { added.Add(npc); return true; }));
            Assert.AreEqual(0, added.Single().EquippedObjects.Count, "never dressed: the default weenie wears nothing");
            Assert.AreEqual(2.0f, added.Single().ObjScale.Value, 0.0001f, "the default look is scaled too");

            Assert.IsTrue(AvatarManager.TrySetLook(AvatarId, LookJson(Piece(Jerkin, Chest), Piece(Breeches, Legs)), null));
            added.Single().Destroy();
            added.Clear();

            // Pin the consumer: the NPC that reaches the landblock's add already wears the stored pieces.
            var wornAtAdd = new List<uint[]>();
            Assert.AreEqual(1, AvatarNpcs.SpawnSeated(0x0090, 0, npc => { wornAtAdd.Add(Worn(npc)); added.Add(npc); return true; }));
            CollectionAssert.AreEqual(new[] { Breeches, Jerkin }, wornAtAdd.Single());
            Assert.AreEqual(2, added.Single().Gender);
            Assert.AreEqual(2.2f, added.Single().ObjScale.Value, 0.0001f);
        }

        [TestMethod]
        public void Redress_reaches_summoned_and_seated()
        {
            var seat = Outdoors();
            store.Avatars.Add(Row(AvatarId, Monarch, seat: seat));
            var a = Init();

            monarch.Location = new Position(0x00900011, 60f, 40f, 0f, 0f, 0f, 0.7071f, 0.7071f, 0);
            Assert.IsTrue(AvatarNpcs.TrySummon(monarch, a, out var refusal), refusal);
            var oldSummoned = AvatarNpcs.SummonedNpc(AvatarId);
            Assert.AreEqual(T.SummonSeconds, expiries.Single().Seconds);

            Assert.AreEqual(1, AvatarNpcs.SpawnSeated(0x0090, 0, npc => true));
            var oldSeated = AvatarNpcs.SeatedNpc(AvatarId);
            oldSeated.Location = new Position(0x00900011, 50f, 50f, 0f, 0f, 0f, 1f, 0f, 0); // turned to face a customer

            Assert.IsTrue(AvatarManager.TrySetLook(AvatarId, LookJson(Piece(Jerkin, Chest)), null));
            now += 100;
            var result = AvatarNpcs.Redress(AvatarId);

            // The summoned replacement ran inline on the (world-thread) caller; the seated one is only queued.
            Assert.AreEqual(new AvatarNpcs.RedressResult(2, false), result);
            Assert.AreEqual(0, worldQueue.Count, "nothing is handed on to the world queue: the caller is the world thread");
            Assert.AreEqual(1, landblockQueue.Count, "the seated replacement runs on its landblock's queue");
            Assert.IsFalse(oldSeated.IsDestroyed, "the seated NPC is not touched until its landblock runs the work");

            var newSummoned = AvatarNpcs.SummonedNpc(AvatarId);
            Assert.IsTrue(oldSummoned.IsDestroyed);
            Assert.AreNotSame(oldSummoned, newSummoned);
            Assert.AreEqual(AvatarId, newSummoned.AvatarId);
            CollectionAssert.AreEqual(new[] { Jerkin }, Worn(newSummoned));
            var (placedNpc, placedAt) = replacedAt.Single();
            Assert.AreSame(newSummoned, placedNpc);
            Assert.AreEqual(oldSummoned.Location.Cell, placedAt.Cell);
            Assert.AreEqual(60f, placedAt.PositionX);
            Assert.AreEqual(0.7071f, placedAt.RotationZ, "same heading");
            Assert.AreSame(newSummoned, expiries.Last().Npc);
            Assert.AreEqual(T.SummonSeconds - 100, expiries.Last().Seconds, 0.001, "the replacement keeps the original expiry");
            Assert.AreEqual(0, fallbackCalls, "the exact spot was accepted; the ladder never runs");
            Assert.IsFalse(oldSeated.IsDestroyed, "the world thread never touches the seated NPC");

            AvatarNpcs.ExpireSummoned(oldSummoned);
            Assert.AreSame(newSummoned, AvatarNpcs.SummonedNpc(AvatarId), "the old NPC's timer never clears the replacement's slot");

            RunAll(landblockQueue);
            var newSeated = AvatarNpcs.SeatedNpc(AvatarId);
            Assert.IsTrue(oldSeated.IsDestroyed);
            Assert.AreSame(newSeated, addedToLandblock.Single());
            CollectionAssert.AreEqual(new[] { Jerkin }, Worn(newSeated));
            Assert.AreEqual(oldSeated.Location.Cell, newSeated.Location.Cell);
            Assert.AreEqual(1f, newSeated.Location.RotationZ, "same heading as the old NPC");
            Assert.AreEqual(seat.RotationW, newSeated.Home.RotationW, "Home stays the Seat");
            Assert.AreEqual(0, AvatarNpcs.SpawnSeated(0x0090, 0, npc => true), "the replacement holds the Seat's slot");
        }

        [TestMethod]
        public void Redress_after_the_npc_changed_leaves_the_newer_one()
        {
            store.Avatars.Add(Row(AvatarId, Monarch, seat: Outdoors()));
            var a = Init();
            Assert.IsTrue(AvatarNpcs.TrySummon(monarch, a, out _));
            Assert.AreEqual(1, AvatarNpcs.SpawnSeated(0x0090, 0, npc => true));
            var seatedBefore = AvatarNpcs.SeatedNpc(AvatarId);

            // A summon from another thread lands while the replacement is being placed.
            AvatarVendor newer = null;
            AvatarVendor replacement = null;
            AvatarNpcs.SummonReplacer = (npc, at) =>
            {
                replacement = npc;
                Assert.IsTrue(AvatarNpcs.TrySummon(monarch, a, out _));
                newer = AvatarNpcs.SummonedNpc(AvatarId);
                return true;
            };

            var result = AvatarNpcs.Redress(AvatarId);

            // Before the queued seated work runs, the seated NPC goes with its landblock.
            seatedBefore.Destroy();
            RunAll(landblockQueue);

            Assert.IsNotNull(newer);
            Assert.AreSame(newer, AvatarNpcs.SummonedNpc(AvatarId), "a stale redress never replaces a newer summon");
            Assert.IsFalse(newer.IsDestroyed);
            Assert.IsTrue(replacement.IsDestroyed, "the losing replacement is not leaked");
            Assert.IsFalse(result.SummonEnded, "the newer summon stands, so the summon did not end");
            Assert.AreEqual(0, addedToLandblock.Count, "a seated NPC already gone is left to the respawn path");
        }

        /// <summary>Avatar 7 seated at Outdoors() with its NPC standing, and summoned in front of the monarch; the look is then changed.</summary>
        private (AvatarVendor Summoned, AvatarVendor Seated, Position Seat) SummonSeatAndChangeLook()
        {
            var seat = Outdoors();
            store.Avatars.Add(Row(AvatarId, Monarch, seat: seat));
            var a = Init();
            Assert.IsTrue(AvatarNpcs.TrySummon(monarch, a, out var refusal), refusal);
            Assert.AreEqual(1, AvatarNpcs.SpawnSeated(0x0090, 0, npc => true));
            var summonedNpc = AvatarNpcs.SummonedNpc(AvatarId);
            var seatedNpc = AvatarNpcs.SeatedNpc(AvatarId);
            Assert.IsTrue(AvatarManager.TrySetLook(AvatarId, LookJson(Piece(Jerkin, Chest)), null));
            return (summonedNpc, seatedNpc, seat);
        }

        [TestMethod]
        public void Redress_summoned_refused_at_its_spot_uses_the_ladder()
        {
            var (old, _, _) = SummonSeatAndChangeLook();
            var exact = new List<AvatarVendor>();
            var ladder = new List<(AvatarVendor Npc, Position Around)>();
            AvatarNpcs.SummonReplacer = (npc, at) => { exact.Add(npc); return false; };
            AvatarNpcs.SummonFallbackPlacer = (npc, around) =>
            {
                ladder.Add((npc, around));
                npc.Location = new Position(around.Cell, around.PositionX + 2f, around.PositionY, around.PositionZ, 0f, 0f, 0f, 1f, around.Instance);
                return true;
            };

            var result = AvatarNpcs.Redress(AvatarId);

            Assert.IsFalse(result.SummonEnded, "the ladder placed it");
            var replacement = AvatarNpcs.SummonedNpc(AvatarId);
            Assert.IsNotNull(replacement, "the map holds the replacement");
            Assert.AreNotSame(old, replacement);
            Assert.IsTrue(old.IsDestroyed);
            Assert.IsFalse(replacement.IsDestroyed);
            Assert.AreSame(replacement, exact.Single(), "the exact spot is tried first");
            Assert.AreSame(replacement, ladder.Single().Npc, "then the ladder");
            Assert.AreEqual(old.Location.PositionX, ladder.Single().Around.PositionX, "the ladder is centred on the old spot");
            CollectionAssert.AreEqual(new[] { Jerkin }, Worn(replacement));
            Assert.AreSame(replacement, expiries.Last().Npc, "the replacement is timed");
        }

        [TestMethod]
        public void Redress_summoned_refused_everywhere_ends_the_summon()
        {
            var (old, seatedNpc, _) = SummonSeatAndChangeLook();
            AvatarVendor tried = null;
            AvatarNpcs.SummonReplacer = (npc, at) => { tried = npc; return false; };
            AvatarNpcs.SummonFallbackPlacer = (npc, around) => false;
            var timed = expiries.Count;

            var result = AvatarNpcs.Redress(AvatarId);

            Assert.IsTrue(result.SummonEnded, "the Keeper is told the summon ended");
            Assert.AreEqual(1, result.Replaced, "only the queued seated replacement");
            Assert.IsNull(AvatarNpcs.SummonedNpc(AvatarId), "the summon ends");
            Assert.IsTrue(old.IsDestroyed);
            Assert.IsNotNull(tried);
            Assert.IsTrue(tried.IsDestroyed, "the unplaced replacement is not leaked");
            Assert.AreEqual(timed, expiries.Count, "nothing is timed");
            Assert.AreSame(seatedNpc, AvatarNpcs.SeatedNpc(AvatarId), "the seated NPC is untouched by the world-thread work");
            Assert.IsFalse(seatedNpc.IsDestroyed);
        }

        [TestMethod]
        public void Redress_seated_add_fails_once_then_respawns_at_the_seat()
        {
            var (_, old, seat) = SummonSeatAndChangeLook();
            old.Location = new Position(0x00900011, 55f, 45f, 0f, 0f, 0f, 1f, 0f, 0); // wandered and turned
            var attempts = new List<AvatarVendor>();
            AvatarNpcs.SeatLandblockEnqueue = (p, work) =>
            {
                landblockQueue.Add(() => work(npc => { attempts.Add(npc); return attempts.Count > 1; }));
                return true;
            };

            AvatarNpcs.Redress(AvatarId);
            RunAll(landblockQueue);

            var live = AvatarNpcs.SeatedNpc(AvatarId);
            Assert.AreEqual(2, attempts.Count, "one retry");
            Assert.IsTrue(attempts[0].IsDestroyed, "the refused replacement is not leaked");
            Assert.AreSame(attempts[1], live, "the retry holds the Seat's slot");
            Assert.IsFalse(live.IsDestroyed);
            Assert.IsTrue(old.IsDestroyed);
            Assert.AreEqual(seat.Cell, live.Location.Cell);
            Assert.AreEqual(seat.PositionX, live.Location.PositionX, "the retry stands at the Seat itself");
            Assert.AreEqual(seat.RotationW, live.Location.RotationW);
            CollectionAssert.AreEqual(new[] { Jerkin }, Worn(live));
        }

        [TestMethod]
        public void Redress_build_failure_keeps_the_old_npcs()
        {
            var (oldSummoned, oldSeated, _) = SummonSeatAndChangeLook();
            AvatarNpcs.NpcBuilder = () => throw new InvalidOperationException("no weenie");

            var result = AvatarNpcs.Redress(AvatarId);
            RunAll(landblockQueue);

            Assert.IsFalse(result.SummonEnded, "the old summoned NPC is kept");
            Assert.AreSame(oldSummoned, AvatarNpcs.SummonedNpc(AvatarId));
            Assert.AreSame(oldSeated, AvatarNpcs.SeatedNpc(AvatarId));
            Assert.IsFalse(oldSummoned.IsDestroyed);
            Assert.IsFalse(oldSeated.IsDestroyed);
            Assert.AreEqual(0, replacedAt.Count);
            Assert.AreEqual(0, addedToLandblock.Count);
        }

        [TestMethod]
        public void Redress_result_summon_ended_only_when_both_placements_refuse()
        {
            var (_, _, _) = SummonSeatAndChangeLook();
            AvatarNpcs.SummonReplacer = (npc, at) => false;

            AvatarNpcs.SummonFallbackPlacer = (npc, around) => { npc.Location = new Position(around); return true; };
            var ladder = AvatarNpcs.Redress(AvatarId);
            Assert.IsFalse(ladder.SummonEnded, "the ladder succeeded");
            Assert.IsNotNull(AvatarNpcs.SummonedNpc(AvatarId));

            AvatarNpcs.SummonFallbackPlacer = (npc, around) => false;
            var refused = AvatarNpcs.Redress(AvatarId);
            Assert.IsTrue(refused.SummonEnded, "both placements refused");
            Assert.IsNull(AvatarNpcs.SummonedNpc(AvatarId));
        }

        [TestMethod]
        public void Redress_off_the_world_thread_logs_and_still_runs()
        {
            var (old, _, _) = SummonSeatAndChangeLook();
            AvatarNpcs.OnWorldThread = () => false;

            var result = AvatarNpcs.Redress(AvatarId);

            Assert.AreEqual(new AvatarNpcs.RedressResult(2, false), result, "an error is logged, nothing throws, the work runs");
            Assert.IsTrue(old.IsDestroyed);
            Assert.IsNotNull(AvatarNpcs.SummonedNpc(AvatarId));
        }

        [TestMethod]
        public void Redress_replacements_are_level_synced()
        {
            var (_, _, _) = SummonSeatAndChangeLook();
            var a = AvatarManager.Get(AvatarId);
            var applied = new List<(AvatarVendor Npc, int Level)>();
            AvatarLevelUp.ApplyToNpc = (npc, level) => applied.Add((npc, level));

            // A level-up lands after Build read the level and before the replacement takes its slot, so no dispatch can see it.
            AvatarNpcs.SummonReplacer = (npc, at) => { npc.Location = new Position(at); a.LevelXp = 1_000_000; return true; };
            var levelBefore = AvatarLevelUp.LiveLevel(AvatarId);

            AvatarNpcs.Redress(AvatarId);
            var summonedReplacement = AvatarNpcs.SummonedNpc(AvatarId);
            var levelAfter = AvatarLevelUp.LiveLevel(AvatarId).Value;
            Assert.AreNotEqual(levelBefore, levelAfter, "fixture: the XP bump must change the level");
            CollectionAssert.AreEqual(new[] { (summonedReplacement, levelAfter) }, applied.ToArray(), "the summoned replacement is synced once it holds its slot");

            applied.Clear();
            landblockQueue.Clear(); // drop the first Redress's seated work: the second one carries the level-up
            AvatarNpcs.SeatLandblockEnqueue = (p, work) =>
            {
                landblockQueue.Add(() => work(npc => { a.LevelXp = 50_000_000; return true; }));
                return true;
            };
            Assert.IsTrue(AvatarManager.TrySetLook(AvatarId, LookJson(Piece(Breeches, Legs)), null));
            AvatarNpcs.SummonReplacer = (npc, at) => { npc.Location = new Position(at); return true; };
            AvatarNpcs.Redress(AvatarId);
            applied.Clear(); // the summoned one again; only the seated one is under test here
            RunAll(landblockQueue);

            var seatedReplacement = AvatarNpcs.SeatedNpc(AvatarId);
            var seatedLevel = AvatarLevelUp.LiveLevel(AvatarId).Value;
            Assert.AreNotEqual(levelAfter, seatedLevel, "fixture: the second XP bump must change the level");
            CollectionAssert.AreEqual(new[] { (seatedReplacement, seatedLevel) }, applied.ToArray(), "the seated replacement is synced once it holds its slot");
        }

        [TestMethod]
        public void Redress_with_no_live_npc_queues_nothing()
        {
            store.Avatars.Add(Row(AvatarId, Monarch));
            Init();

            Assert.AreEqual(new AvatarNpcs.RedressResult(0, false), AvatarNpcs.Redress(AvatarId));
            Assert.AreEqual(new AvatarNpcs.RedressResult(0, false), AvatarNpcs.Redress(999));

            Assert.AreEqual(0, worldQueue.Count);
            Assert.AreEqual(0, landblockQueue.Count);
        }

        // ---- tunable --------------------------------------------------------------------------------------------

        [TestMethod]
        public void Tuning_reads_dress_cooldown()
        {
            Assert.AreEqual(3600L, AvatarTuning.Defaults.DressCooldownSeconds);
            Assert.AreEqual(3600L, DefaultPropertyManager.DefaultLongProperties[DressCooldownKey].Item);

            try
            {
                // FromPropertyManager reads every key; an unseeded read would hit the database.
                foreach (var kv in DefaultPropertyManager.DefaultBooleanProperties.Where(kv => kv.Key.StartsWith("allegiance_avatar_")))
                    Assert.IsTrue(PropertyManager.ModifyBool(kv.Key, kv.Value.Item));
                foreach (var kv in DefaultPropertyManager.DefaultLongProperties.Where(kv => kv.Key.StartsWith("allegiance_avatar_")))
                    Assert.IsTrue(PropertyManager.ModifyLong(kv.Key, kv.Value.Item));
                foreach (var kv in DefaultPropertyManager.DefaultDoubleProperties.Where(kv => kv.Key.StartsWith("allegiance_avatar_")))
                    Assert.IsTrue(PropertyManager.ModifyDouble(kv.Key, kv.Value.Item, init: true));

                Assert.IsTrue(PropertyManager.ModifyLong(DressCooldownKey, 1234));
                Assert.AreEqual(1234L, AvatarTuning.FromPropertyManager().DressCooldownSeconds);
            }
            finally
            {
                foreach (var kv in DefaultPropertyManager.DefaultBooleanProperties.Where(kv => kv.Key.StartsWith("allegiance_avatar_")))
                    PropertyManager.ModifyBool(kv.Key, kv.Value.Item);
                foreach (var kv in DefaultPropertyManager.DefaultLongProperties.Where(kv => kv.Key.StartsWith("allegiance_avatar_")))
                    PropertyManager.ModifyLong(kv.Key, kv.Value.Item);
                foreach (var kv in DefaultPropertyManager.DefaultDoubleProperties.Where(kv => kv.Key.StartsWith("allegiance_avatar_")))
                    PropertyManager.ModifyDouble(kv.Key, kv.Value.Item, init: true);
            }
        }

        // ---- store fake -----------------------------------------------------------------------------------------

        private sealed class Store : IAvatarStore
        {
            public readonly List<AllegianceAvatar> Avatars = new List<AllegianceAvatar>();
            public readonly List<AllegianceAvatar> Saved = new List<AllegianceAvatar>();
            public bool LoadAllReturnsNull;

            public List<AllegianceAvatar> LoadAll() => LoadAllReturnsNull ? null : Avatars.ToList();
            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => new List<AllegianceAvatarContribution>();

            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done)
            {
                Saved.AddRange(a);
                done(true);
            }

            public void Insert(AllegianceAvatar row, Action<bool, uint> done) => done(true, 0);
        }
    }
}
