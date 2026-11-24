using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.BalanceTrials;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// Task 6 wiring: TrialStarter's refusal order, LiveTrialWorld's foreign-creature count driving the session, the manager's
    /// gone-room sweep, and source pins on every server site the trial hooks into (Player_Death, Player_Location,
    /// Creature_Death). A Player is only ever an uninitialized object whose guid is written through UnsafeAccessor; no
    /// FieldInfo.SetValue on Player (it runs Player's static constructor and poisons the test process).
    ///
    /// PropertyManager keys read: balance_trials_enabled and balance_trials_data_dir (TrialStarter), plus whatever creature
    /// construction reads (ClassInitialize loads the compiled defaults, as TrialSpawnerTests does). Both trial keys are seeded
    /// per test and restored to their compiled defaults in finally.
    /// </summary>
    [TestClass]
    public class TrialWiringTests
    {
        private const string PlayerDeathPath = "Source/ACE.Server/WorldObjects/Player_Death.cs";
        private const string PlayerLocationPath = "Source/ACE.Server/WorldObjects/Player_Location.cs";
        private const string CreatureDeathPath = "Source/ACE.Server/WorldObjects/Creature_Death.cs";

        private const string EnabledKey = "balance_trials_enabled";
        private const string DataDirKey = "balance_trials_data_dir";

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Guid>k__BackingField")]
        private static extern ref ObjectGuid GuidField(WorldObject wo);

        // Position storage for a bare Player (all declared on WorldObject; the uninitialized object has them null).
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Biota>k__BackingField")]
        private static extern ref Biota BiotaField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "BiotaDatabaseLock")]
        private static extern ref System.Threading.ReaderWriterLockSlim LockField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ephemeralPositions")]
        private static extern ref Dictionary<PositionType, Position> EphemeralPositionsField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "positionCache")]
        private static extern ref Dictionary<PositionType, Position> PositionCacheField(WorldObject wo);

        private static uint nextCreatureGuid = 0x7ED70000;

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestCleanup]
        public void Cleanup()
        {
            PropertyManager.ModifyBool(EnabledKey, DefaultPropertyManager.DefaultBooleanProperties[EnabledKey].Item);
            PropertyManager.ModifyString(DataDirKey, DefaultPropertyManager.DefaultStringProperties[DataDirKey].Item);
            TrialStarter.ExternalConfigDir = () => ACE.Common.ExternalConfigDirectory.Resolve(Program.IsRunningInContainer);
            TrialStarter.DirectoryExists = Directory.Exists;
            TrialManager.ResetForTests();
        }

        // ---- fixtures ----------------------------------------------------------------------------------------

        private static Player BarePlayer(uint guid)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            GuidField(player) = new ObjectGuid(guid);
            return player;
        }

        /// <summary>A bare Player standing at <paramref name="location"/>, carrying an exit stamp, with working position storage.</summary>
        private static Player PlayerAt(uint guid, Position location)
        {
            var player = BarePlayer(guid);
            BiotaField(player) = new Biota { PropertiesPosition = new Dictionary<PositionType, PropertiesPosition>() };
            LockField(player) = new System.Threading.ReaderWriterLockSlim();
            EphemeralPositionsField(player) = new Dictionary<PositionType, Position>();
            PositionCacheField(player) = new Dictionary<PositionType, Position>();
            PositionCacheField(player)[PositionType.Location] = location;
            // The trial's own stamp, written and remembered the way TrialStarter does it.
            var stamp = new Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0);
            PositionCacheField(player)[PositionType.EphemeralRealmExitTo] = stamp;
            player.TrialExitStamp = stamp;
            return player;
        }

        private static Position In(uint instance) => new Position(0x596A010C, 40f, -20f, 0.05f, 0f, 0f, 0f, 1f, instance);

        private static void MoveTo(Player player, Position location) => PositionCacheField(player)[PositionType.Location] = location;

        private static Creature NewCreature(bool attackable)
        {
            TestGameTables.EnsureInitialized();

            var weenie = new Weenie
            {
                WeenieClassId = 0x7ED7,
                ClassName = "trialwiringprobe",
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 100 },
                PropertiesBool = new Dictionary<PropertyBool, bool> { [PropertyBool.Attackable] = attackable },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Trial Wiring Probe" },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 100 },
                },
            };

            return new Creature(weenie, new ObjectGuid(nextCreatureGuid++));
        }

        private sealed class FakeWorld : ITrialWorld
        {
            public bool RoomLoaded { get; set; } = true;
            public bool PlayerOnline { get; set; } = true;
            public bool PlayerInRoom { get; set; } = true;
            public int DestroyCalls;
            public int ReturnCalls;
            private uint next = 500;

            public int LiveForeignCreatures() => 0;
            public bool IsAlive(uint guid) => true;

            public void DestroyAll() => DestroyCalls++;
            public void ReturnPlayerToStart() => ReturnCalls++;

            public IReadOnlyList<uint> SpawnWave(IReadOnlyList<DungeonSpawnPlanEntry> wave, TrialRep rep) => new[] { next++ };
        }

        private static TrialScenario Scenario(int reps = 3)
            => new TrialScenario("s", "r", null, 1, null, "off", reps, 600, 0);

        private static TrialRoom Room()
            => new TrialRoom("r", 0x596A, new DungeonSpawnPointDef { Cell = 0x596A010C, X = 40, Y = -20, Z = 0.05f, QW = 1 }, null,
                new List<DungeonSpawnPointDef>(), null);

        private static TrialRep TwoWaveRep()
            => new TrialRep
            {
                Waves = new List<IReadOnlyList<DungeonSpawnPlanEntry>> { new List<DungeonSpawnPlanEntry>(), new List<DungeonSpawnPlanEntry>() },
            };

        private static TrialSession Session(ITrialWorld world, uint playerGuid = 0x59000100, TrialRoom room = null)
            => new TrialSession("uuid-" + playerGuid.ToString("X8"), Scenario(), "sha", null, "asha", room ?? Room(), playerGuid, world, _ => TwoWaveRep());

        // ---- TrialStarter refusals -----------------------------------------------------------------------------

        [TestMethod]
        public void Start_refuses_with_the_setting_off()
        {
            Assert.IsTrue(PropertyManager.ModifyBool(EnabledKey, false), "balance_trials_enabled must be a registered key");

            // Off wins over everything after it: a rep guid, and no data dir at all.
            var player = BarePlayer(TrialRoster.RepSlots[0].Guid);
            TrialStarter.ExternalConfigDir = () => null;

            Assert.IsFalse(TrialStarter.TryStart(player, "t400-arch-solo", "archmage-400", out var refusal));
            StringAssert.Contains(refusal, EnabledKey);
            Assert.IsNull(player.TrialSession, "a refusal binds nothing");
            Assert.AreEqual(0, TrialManager.SessionCount, "a refusal registers nothing");
        }

        [TestMethod]
        public void Rebuild_refuses_with_the_setting_off_before_reading_anything()
        {
            var dirReads = 0;
            TrialStarter.ExternalConfigDir = () => { dirReads++; return null; };
            Assert.IsTrue(PropertyManager.ModifyString(DataDirKey, ""));

            Assert.IsTrue(PropertyManager.ModifyBool(EnabledKey, false));
            var reply = ACE.Server.Command.Handlers.TrialCommands.RebuildReply(null, "archmage-400", 1);
            StringAssert.StartsWith(reply, "rebuild refused: ");
            StringAssert.Contains(reply, EnabledKey);
            Assert.AreEqual(0, dirReads, "off: the data dir is never resolved, so nothing is loaded or copied");

            // Control: switched on, the same call goes on to resolve the data dir (and is refused there, naming that setting).
            Assert.IsTrue(PropertyManager.ModifyBool(EnabledKey, true));
            reply = ACE.Server.Command.Handlers.TrialCommands.RebuildReply(null, "archmage-400", 1);
            Assert.AreEqual(1, dirReads);
            StringAssert.Contains(reply, DataDirKey);
            Assert.IsFalse(reply.Contains(EnabledKey), reply);
        }

        [TestMethod]
        public void Start_refuses_without_a_data_dir_and_names_the_setting()
        {
            Assert.IsTrue(PropertyManager.ModifyBool(EnabledKey, true));
            Assert.IsTrue(PropertyManager.ModifyString(DataDirKey, ""));

            var external = Path.Combine(Path.GetTempPath(), "trialwiring-" + Guid.NewGuid().ToString("N"));
            TrialStarter.ExternalConfigDir = () => external;
            TrialStarter.DirectoryExists = _ => false;

            var player = BarePlayer(TrialRoster.RepSlots[0].Guid);

            Assert.IsFalse(TrialStarter.TryStart(player, "t400-arch-solo", "archmage-400", out var refusal));
            StringAssert.Contains(refusal, DataDirKey);
            StringAssert.Contains(refusal, "BalanceTrials");
            Assert.IsNull(player.TrialSession);
            Assert.AreEqual(0, TrialManager.SessionCount);

            // No external config dir at all: still refused, still naming the setting.
            TrialStarter.ExternalConfigDir = () => null;
            Assert.IsFalse(TrialStarter.TryStart(player, "t400-arch-solo", "archmage-400", out refusal));
            StringAssert.Contains(refusal, DataDirKey);
        }

        [TestMethod]
        public void Start_refuses_a_non_rep_character()
        {
            var dataDir = Path.Combine(Path.GetTempPath(), "trialwiring-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDir);

            try
            {
                Assert.IsTrue(PropertyManager.ModifyBool(EnabledKey, true));
                Assert.IsTrue(PropertyManager.ModifyString(DataDirKey, dataDir));

                // A guid that is not on TrialRoster.RepSlots (a master slot, and an ordinary player guid).
                foreach (var guid in new[] { TrialRoster.MasterSlot(0), 0x50000001u })
                {
                    var player = BarePlayer(guid);
                    Assert.IsFalse(TrialStarter.TryStart(player, "t400-arch-solo", "archmage-400", out var refusal));
                    StringAssert.Contains(refusal, "not a trial rep character", $"guid 0x{guid:X8}");
                    Assert.IsNull(player.TrialSession);
                }

                // Order: "already in a session" is checked BEFORE the rep check (a non-rep with a live session hears that first).
                var bound = BarePlayer(0x50000002u);
                bound.TrialSession = Session(new FakeWorld(), 0x50000002u);
                Assert.IsFalse(TrialStarter.TryStart(bound, "t400-arch-solo", "archmage-400", out var first));
                StringAssert.Contains(first, "already in a trial");

                // Control: a rep guid passes the rep check and is refused later, by the (empty) data directory's scenarios.
                var rep = BarePlayer(TrialRoster.RepSlots[3].Guid);
                Assert.IsFalse(TrialStarter.TryStart(rep, "t400-arch-solo", "archmage-400", out var later));
                Assert.IsFalse(later.Contains("not a trial rep character"), later);
                StringAssert.Contains(later, "scenario");
                Assert.IsNull(rep.TrialSession);
                Assert.AreEqual(0, TrialManager.SessionCount);
            }
            finally
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }

        [TestMethod]
        public void Start_refuses_a_room_with_live_creatures()
        {
            // A hostile base creature left in the copy (P_TrialRep null, attackable): the session never becomes Ready.
            var hostile = NewCreature(attackable: true);
            var objects = new List<WorldObject> { hostile };

            var world = new LiveTrialWorld(null, 0x7FFF0001, 0x59000100, Room(), () => true, () => objects, _ => null);
            var session = Session(world);
            world.Bind(session);

            session.Tick(0);

            Assert.AreEqual(TrialPhase.Aborted, session.Phase);
            Assert.AreEqual("room not empty", session.LastRefusal);
            Assert.IsFalse(session.TryNext(1, out var refusal));
            StringAssert.Contains(refusal, "Aborted");

            // Control: what the count must NOT see. A trial creature, a non-attackable creature and a player are not foreign.
            var trialCreature = NewCreature(attackable: true);
            trialCreature.P_TrialRep = new TrialRep();
            var friendly = NewCreature(attackable: false);
            var player = BarePlayer(0x59000100);

            var clean = new List<WorldObject> { trialCreature, friendly, player };
            Assert.AreEqual(0, LiveTrialWorld.CountForeign(clean));
            Assert.AreEqual(1, LiveTrialWorld.CountForeign(clean.Append(hostile)));

            var cleanWorld = new LiveTrialWorld(null, 0x7FFF0002, 0x59000100, Room(), () => true, () => clean, _ => null);
            var ready = Session(cleanWorld);
            cleanWorld.Bind(ready);
            ready.Tick(0);
            Assert.AreEqual(TrialPhase.Ready, ready.Phase);

            // Not loaded yet: the count is not read and the session stays Preparing.
            var loading = false;
            var lateWorld = new LiveTrialWorld(null, 0x7FFF0003, 0x59000100, Room(), () => loading, () => objects, _ => null);
            var late = Session(lateWorld);
            late.Tick(0);
            Assert.AreEqual(TrialPhase.Preparing, late.Phase);
            loading = true;
            late.Tick(1);
            Assert.AreEqual(TrialPhase.Aborted, late.Phase, "the hostile creature is seen once the room loads");
        }

        // ---- the manager -------------------------------------------------------------------------------------

        [TestMethod]
        public void Manager_tick_ends_a_session_whose_landblock_is_gone()
        {
            TrialManager.ResetForTests();
            TrialManager.LandblockLookup = _ => null;
            TrialManager.OnlinePlayer = _ => null;

            var world = new FakeWorld();
            var session = Session(world, 0x59000101);
            session.Tick(0);
            Assert.IsTrue(session.TryNext(1, out var r), r);
            Assert.AreEqual(TrialPhase.Running, session.Phase);

            TrialManager.RegisterForTests(session, 0x7FFF0010, seenLive: true);

            // A second session registered just now, never seen live: inside the arrival grace it is kept.
            var fresh = Session(new FakeWorld(), 0x59000102);
            TrialManager.RegisterForTests(fresh, 0x7FFF0011, seenLive: false);

            Assert.IsTrue(TrialManager.TryGetByInstance(0x7FFF0010, out _));
            Assert.IsTrue(TrialManager.TryGetByPlayer(0x59000101, out _));

            TrialManager.TickAt(TrialManager.Now());

            Assert.AreEqual(TrialPhase.Aborted, session.Phase);
            Assert.AreEqual(TrialEventKinds.EndForfeit, session.LastEndState);
            StringAssert.Contains(session.LastRefusal, "landblock gone");
            Assert.AreEqual(1, world.DestroyCalls, "the rep's creatures are destroyed");
            Assert.IsFalse(TrialManager.TryGetByInstance(0x7FFF0010, out _), "the session left the registry");
            Assert.IsFalse(TrialManager.TryGetByPlayer(0x59000101, out _));
            Assert.IsFalse(TrialManager.TryGetInstance(session, out _));

            Assert.IsTrue(TrialManager.TryGetByInstance(0x7FFF0011, out var kept), "a session inside its arrival grace is kept");
            Assert.AreSame(fresh, kept);
            Assert.AreEqual(TrialPhase.Preparing, fresh.Phase);

            // Past the grace with the room never seen: ended too.
            TrialManager.TickAt(TrialManager.Now() + TrialManager.ArrivalGraceSeconds + 1);
            Assert.IsFalse(TrialManager.TryGetByInstance(0x7FFF0011, out _));
            Assert.AreEqual(TrialPhase.Aborted, fresh.Phase);
        }

        [TestMethod]
        public void Start_position_is_the_room_start_in_the_session_instance()
        {
            TrialManager.ResetForTests();
            TrialManager.OnlinePlayer = _ => null;

            var player = BarePlayer(0x59000103);
            Assert.IsNull(TrialManager.StartPositionFor(player), "no session, no trial start");

            var session = Session(new FakeWorld(), 0x59000103);
            player.TrialSession = session;
            Assert.IsNull(TrialManager.StartPositionFor(player), "a session that is not registered has no room");

            TrialManager.RegisterForTests(session, 0x7FFF0020, seenLive: true);
            var p = TrialManager.StartPositionFor(player);

            Assert.IsNotNull(p);
            Assert.AreEqual(0x596A010Cu, p.Cell);
            Assert.AreEqual(0x7FFF0020u, p.Instance);
            Assert.AreEqual(40f, p.PositionX);
            Assert.AreEqual(-20f, p.PositionY);
        }

        // ---- Player_Trial -----------------------------------------------------------------------------------

        private static TrialSession RunningSession(FakeWorld world, uint playerGuid)
        {
            var session = Session(world, playerGuid);
            session.Tick(0);
            Assert.IsTrue(session.TryNext(1, out var r), r);
            Assert.AreEqual(TrialPhase.Running, session.Phase);
            return session;
        }

        [TestMethod]
        public void Trial_death_latch_needs_a_session_in_the_same_instance()
        {
            TrialManager.ResetForTests();
            TrialManager.LandblockLookup = _ => null;
            TrialManager.OnlinePlayer = _ => null;

            // No session: false, and the latch is clear.
            var lone = PlayerAt(0x59000110, In(0x7FFF0030));
            Assert.IsFalse(lone.TryBeginTrialDeath());
            Assert.IsFalse(lone.TrialDeathInProgress);

            // A session registered for another instance: false, and the rep is not touched.
            var player = PlayerAt(0x59000111, In(0x7FFF0031));
            var world = new FakeWorld();
            var session = RunningSession(world, 0x59000111);
            player.TrialSession = session;
            TrialManager.RegisterForTests(session, 0x7FFF0032, seenLive: true);

            Assert.IsFalse(player.TryBeginTrialDeath(), "a death outside the room is an ordinary death");
            Assert.IsFalse(player.TrialDeathInProgress);
            Assert.AreEqual(TrialPhase.Running, session.Phase);

            // A bound session that is not registered at all: false.
            var unregistered = PlayerAt(0x59000112, In(0x7FFF0032));
            unregistered.TrialSession = Session(new FakeWorld(), 0x59000112);
            Assert.IsFalse(unregistered.TryBeginTrialDeath());

            // Control: same instance, latched; EndTrialDeath clears it.
            MoveTo(player, In(0x7FFF0032));
            Assert.IsTrue(player.TryBeginTrialDeath());
            Assert.IsTrue(player.TrialDeathInProgress);
            player.EndTrialDeath();
            Assert.IsFalse(player.TrialDeathInProgress);
        }

        [TestMethod]
        public void Leaving_the_room_unbinds_ends_the_session_and_clears_the_exit_stamp()
        {
            TrialManager.ResetForTests();
            TrialManager.LandblockLookup = _ => null;
            TrialManager.OnlinePlayer = _ => null;

            var player = PlayerAt(0x59000113, In(0x7FFF0040));
            var world = new FakeWorld();
            var session = RunningSession(world, 0x59000113);
            player.TrialSession = session;
            TrialManager.RegisterForTests(session, 0x7FFF0040, seenLive: true);

            // In the room: nothing happens.
            player.CheckTrialInstanceExit();
            Assert.AreSame(session, player.TrialSession);
            Assert.IsNotNull(player.GetPosition(PositionType.EphemeralRealmExitTo));
            Assert.AreEqual(TrialPhase.Running, session.Phase);

            // Recalled out: unbound, the rep ends forfeit with its creatures destroyed, the session leaves the registry, and
            // the exit stamp is spent.
            MoveTo(player, new Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0));
            player.CheckTrialInstanceExit();

            Assert.IsNull(player.TrialSession);
            Assert.IsFalse(player.TrialActive, "TrialActive reads the binding");
            Assert.AreEqual(TrialPhase.Aborted, session.Phase);
            Assert.AreEqual(TrialEventKinds.EndForfeit, session.LastEndState);
            Assert.AreEqual(1, world.DestroyCalls);
            Assert.IsFalse(TrialManager.TryGetByInstance(0x7FFF0040, out _));
            Assert.IsNull(player.GetPosition(PositionType.EphemeralRealmExitTo), "the exit stamp is cleared");

            // Once only: a second reconcile finds no session.
            player.CheckTrialInstanceExit();
            Assert.AreEqual(1, world.DestroyCalls);
        }

        [TestMethod]
        public void Entry_pending_ignores_teleports_until_the_first_arrival()
        {
            TrialManager.ResetForTests();
            TrialManager.LandblockLookup = _ => null;
            TrialManager.OnlinePlayer = _ => null;

            // Bound as TrialStarter binds: before the entry teleport lands, with the window open.
            var outside = new Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0);
            var player = PlayerAt(0x59000114, outside);
            var session = Session(new FakeWorld(), 0x59000114);
            player.TrialEntryPending = true;
            player.TrialSession = session;
            TrialManager.RegisterForTests(session, 0x7FFF0050, seenLive: true);

            // An earlier teleport completes outside the room: ignored.
            player.CheckTrialInstanceExit();
            Assert.AreSame(session, player.TrialSession, "a teleport before the entry lands is not a way out");
            Assert.IsTrue(TrialManager.TryGetByInstance(0x7FFF0050, out _));
            Assert.IsNotNull(player.GetPosition(PositionType.EphemeralRealmExitTo));
            Assert.AreNotEqual(TrialPhase.Aborted, session.Phase);

            // The entry lands: the window closes.
            MoveTo(player, In(0x7FFF0050));
            player.CheckTrialInstanceExit();
            Assert.IsFalse(player.TrialEntryPending);
            Assert.AreSame(session, player.TrialSession);

            // Now leaving ends it.
            MoveTo(player, outside);
            player.CheckTrialInstanceExit();
            Assert.IsNull(player.TrialSession);
            Assert.AreEqual(TrialPhase.Aborted, session.Phase);
            Assert.IsFalse(TrialManager.TryGetByInstance(0x7FFF0050, out _));
        }

        [TestMethod]
        public void Only_the_trials_own_exit_stamp_is_cleared()
        {
            TrialManager.ResetForTests();
            TrialManager.LandblockLookup = _ => null;
            TrialManager.OnlinePlayer = _ => null;

            var outside = new Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0);

            // Scenario C: the player left the room through a portal into another private realm, which stamped its own exit.
            var player = PlayerAt(0x59000120, In(0x7FFF0080));
            var session = RunningSession(new FakeWorld(), 0x59000120);
            player.TrialSession = session;
            TrialManager.RegisterForTests(session, 0x7FFF0080, seenLive: true);

            var foreign = new Position(0x00070100, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0x7FFF00F0);
            PositionCacheField(player)[PositionType.EphemeralRealmExitTo] = foreign;
            MoveTo(player, new Position(0x596A0100, 0f, -20f, 0.05f, 0f, 0f, 0f, 1f, 0x7FFF00F1));

            player.CheckTrialInstanceExit();

            Assert.IsNull(player.TrialSession, "the trial still ends");
            Assert.AreSame(foreign, player.GetPosition(PositionType.EphemeralRealmExitTo), "another realm's stamp survives");
            Assert.IsNull(player.TrialExitStamp, "the trial forgets its stamp");

            // Same frame, different instance: not the trial's stamp either.
            var twin = PlayerAt(0x59000121, outside);
            var twinStamp = new Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0x7FFF00F2);
            PositionCacheField(twin)[PositionType.EphemeralRealmExitTo] = twinStamp;
            twin.ClearTrialExitStampIfLeft(0x7FFF0081);
            Assert.AreSame(twinStamp, twin.GetPosition(PositionType.EphemeralRealmExitTo));

            // Scenario A: the trial never stamped (a refused start); a stamp from elsewhere is left alone.
            var unstamped = PlayerAt(0x59000122, outside);
            unstamped.TrialExitStamp = null;
            var existing = unstamped.GetPosition(PositionType.EphemeralRealmExitTo);
            unstamped.ClearTrialExitStampIfLeft(0x7FFF0082);
            Assert.AreSame(existing, unstamped.GetPosition(PositionType.EphemeralRealmExitTo));

            // Control: the trial's own stamp, still in place, is cleared by the manager-side clear (equal by value counts).
            var own = PlayerAt(0x59000123, outside);
            own.TrialExitStamp = new Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0);
            own.ClearTrialExitStampIfLeft(0x7FFF0083);
            Assert.IsNull(own.GetPosition(PositionType.EphemeralRealmExitTo));
            Assert.IsNull(own.TrialExitStamp);

            // Still in the room: the manager-side clear does nothing.
            var inside = PlayerAt(0x59000124, In(0x7FFF0084));
            inside.ClearTrialExitStampIfLeft(0x7FFF0084);
            Assert.IsNotNull(inside.GetPosition(PositionType.EphemeralRealmExitTo));
            Assert.IsNotNull(inside.TrialExitStamp);
        }

        // ---- the manager's room-thread sweep -----------------------------------------------------------------

        [TestMethod]
        public void Sweep_forfeits_and_removes_a_session_whose_player_went_offline()
        {
            TrialManager.ResetForTests();
            TrialManager.OnlinePlayer = _ => null;

            var world = new FakeWorld();
            var session = RunningSession(world, 0x59000115);
            TrialManager.RegisterForTests(session, 0x7FFF0060, seenLive: true);

            // Online and in the room: kept.
            TrialManager.TickSessionForTests(0x7FFF0060, 2);
            Assert.IsTrue(TrialManager.TryGetByInstance(0x7FFF0060, out _));
            Assert.AreEqual(TrialPhase.Running, session.Phase);

            world.PlayerOnline = false;
            TrialManager.TickSessionForTests(0x7FFF0060, 3);

            Assert.AreEqual(TrialPhase.Aborted, session.Phase);
            Assert.AreEqual(TrialEventKinds.EndForfeit, session.LastEndState);
            Assert.AreEqual(1, world.DestroyCalls);
            Assert.AreEqual(0, world.ReturnCalls, "a tick-detected forfeit does not return the player");
            Assert.IsFalse(TrialManager.TryGetByInstance(0x7FFF0060, out _));

            // A Ready session (no rep running) whose player goes offline is ended and removed too.
            var readyWorld = new FakeWorld { PlayerOnline = false };
            var ready = Session(readyWorld, 0x59000116);
            ready.Tick(0);
            TrialManager.RegisterForTests(ready, 0x7FFF0061, seenLive: true);
            TrialManager.TickSessionForTests(0x7FFF0061, 1);
            Assert.AreEqual(TrialPhase.Aborted, ready.Phase);
            Assert.IsFalse(TrialManager.TryGetByInstance(0x7FFF0061, out _));
        }

        [TestMethod]
        public void Sweep_forfeits_a_player_out_of_the_room_only_after_they_arrived()
        {
            TrialManager.ResetForTests();
            TrialManager.OnlinePlayer = _ => null;

            // Not yet arrived (the entry teleport is in flight), inside the grace: kept.
            var world = new FakeWorld { PlayerInRoom = false };
            var session = Session(world, 0x59000117);
            TrialManager.RegisterForTests(session, 0x7FFF0070, seenLive: true);
            var t0 = TrialManager.Now();

            TrialManager.TickSessionForTests(0x7FFF0070, t0);
            Assert.IsTrue(TrialManager.TryGetByInstance(0x7FFF0070, out _), "before the first arrival, out of the room is not leaving");
            Assert.AreEqual(TrialPhase.Ready, session.Phase);

            // Arrives (SeenPlayer), then leaves: the Ready session is aborted and removed.
            world.PlayerInRoom = true;
            TrialManager.TickSessionForTests(0x7FFF0070, t0 + 1);
            Assert.IsTrue(TrialManager.TryGetByInstance(0x7FFF0070, out _));

            world.PlayerInRoom = false;
            TrialManager.TickSessionForTests(0x7FFF0070, t0 + 2);
            Assert.AreEqual(TrialPhase.Aborted, session.Phase);
            StringAssert.Contains(session.LastRefusal, "left the room");
            Assert.IsFalse(TrialManager.TryGetByInstance(0x7FFF0070, out _));

            // Never arrived and past the grace: ended as well.
            var lateWorld = new FakeWorld { PlayerInRoom = false };
            var late = Session(lateWorld, 0x59000118);
            TrialManager.RegisterForTests(late, 0x7FFF0071, seenLive: true);
            TrialManager.TickSessionForTests(0x7FFF0071, TrialManager.Now() + TrialManager.ArrivalGraceSeconds + 1);
            Assert.IsFalse(TrialManager.TryGetByInstance(0x7FFF0071, out _));
        }

        // ---- source pins: Player_Death -----------------------------------------------------------------------

        private const string DieSignature = "protected override void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)";
        private const string TeleportOnDeathSignature = "public void ThreadSafeTeleportOnDeath()";
        private const string DeathItemsSignature = "public List<WorldObject> CalculateDeathItems(Corpse corpse)";
        private const string OlthoiSignature = "public List<WorldObject> CalculateDeathItems_Olthoi(Corpse corpse, bool hadVitae, bool killerIsOlthoiPlayer, bool killerIsPkPlayer)";

        private static string Code(string body) => Regex.Replace(body, @"//[^\n]*", "");

        /// <summary>The guard statement starting at <paramref name="head"/> up to and including its first return.</summary>
        private static string GuardFrom(string body, string head)
        {
            var start = body.IndexOf(head, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"guard head not found: {head}");
            var ret = body.IndexOf("return new List<WorldObject>();", start, StringComparison.Ordinal);
            Assert.IsTrue(ret > start, $"no return after: {head}");
            return body.Substring(start, ret - start);
        }

        [TestMethod]
        public void Each_death_site_names_TrialDeathInProgress()
        {
            var source = PooledLootSourceText.Read(PlayerDeathPath);

            // :263 site - the latch, in Die().
            var die = Code(PooledLootSourceText.MethodBody(source, DieSignature));
            StringAssert.Contains(die, "var trialDeath = TryBeginTrialDeath();");

            // :477 site - the respawn destination: a trial death goes to the room start, ahead of the arena exit branch.
            var tod = Code(PooledLootSourceText.MethodBody(source, TeleportOnDeathSignature));
            const string TrialBranch = "else if (TrialDeathInProgress && TrialManager.StartPositionFor(this) is Position trialStart)";
            StringAssert.Contains(tod, TrialBranch);
            StringAssert.Contains(tod, "newPosition = trialStart;");
            var arenaBranch = tod.IndexOf("else if (SurvivalChallengeDeathInProgress || WaveChallengeDeathInProgress || SpeedChallengeDeathInProgress || ThreadDungeonDeathInProgress)", StringComparison.Ordinal);
            Assert.IsTrue(arenaBranch > tod.IndexOf(TrialBranch, StringComparison.Ordinal), "the trial branch precedes the arena exit branch, so a trial death never leaves the room");

            // The session-gone fallback leaves through the exit stamp and spends it.
            var fallback = tod.IndexOf("else if (TrialDeathInProgress)\n", StringComparison.Ordinal);
            Assert.IsTrue(fallback > tod.IndexOf(TrialBranch, StringComparison.Ordinal), "the session-gone fallback follows the room-start branch");
            var fallbackBody = tod.Substring(fallback, arenaBranch - fallback);
            StringAssert.Contains(fallbackBody, "var trialExit = GetPosition(PositionType.EphemeralRealmExitTo);");
            StringAssert.Contains(fallbackBody, "SetPosition(PositionType.EphemeralRealmExitTo, null);");

            // :543 site - the latch is cleared beside EndSpeedChallengeDeath.
            StringAssert.Contains(tod, "EndSpeedChallengeDeath();\n                    EndTrialDeath();");

            // :769 site - item loss.
            var items = Code(PooledLootSourceText.MethodBody(source, DeathItemsSignature));
            StringAssert.Contains(GuardFrom(items, "if (PropertyManager.GetBool(\"player_death_no_item_loss\").Item"), "|| TrialDeathInProgress");

            // :1392 site - Olthoi slag / PK loot.
            var olthoi = Code(PooledLootSourceText.MethodBody(source, OlthoiSignature));
            StringAssert.Contains(GuardFrom(olthoi, "if (PropertyManager.GetBool(\"player_death_no_item_loss\").Item"), "|| TrialDeathInProgress");
        }

        [TestMethod]
        public void Trial_death_joins_arenaDeath()
        {
            var die = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(PlayerDeathPath), DieSignature));

            const string Latch = "var trialDeath = TryBeginTrialDeath();";
            const string Join = "var arenaDeath = survivalDeath || waveDeath || speedDeath || trialDeath;";

            Assert.AreEqual(1, Regex.Matches(die, Regex.Escape(Latch)).Count, "one latch");
            StringAssert.Contains(die, Join);
            Assert.IsTrue(die.IndexOf(Latch, StringComparison.Ordinal) < die.IndexOf(Join, StringComparison.Ordinal));

            // The waivers trialDeath reaches through arenaDeath: vitae and the enchantment purge.
            StringAssert.Contains(die, "if (!arenaDeath && !IsPKLiteDeath(topDamager) && !IsMule");
            StringAssert.Contains(die, "if (!arenaDeath && !worldEventDeathInProgress && !PvpPlayerRules.WaivesEnchantmentPurge");
        }

        // ---- source pins: Player_Location --------------------------------------------------------------------

        [TestMethod]
        public void Both_teleport_reconcilers_call_CheckTrialInstanceExit()
        {
            var source = PooledLootSourceText.Read(PlayerLocationPath);

            var refused = Code(PooledLootSourceText.MethodBody(source, "public void Teleport(Position _newPosition, bool fromPortal = false, string source = null)"));
            StringAssert.Contains(refused, "CheckSpeedChallengeInstanceExit();\n                CheckTrialInstanceExit();", "the refused-teleport reconcile");

            var complete = Code(PooledLootSourceText.MethodBody(source, "public void OnTeleportComplete()"));
            Assert.AreEqual(1, Regex.Matches(complete, Regex.Escape("CheckTrialInstanceExit();")).Count, "OnTeleportComplete reconciles once");
            Assert.IsTrue(complete.IndexOf("CheckSpeedChallengeInstanceExit();", StringComparison.Ordinal) < complete.IndexOf("CheckTrialInstanceExit();", StringComparison.Ordinal),
                "beside (after) the speed reconcile");
        }

        // ---- source pins: Creature_Death ---------------------------------------------------------------------

        [TestMethod]
        public void Creature_Die_reports_trial_kills_after_the_thread_hook()
        {
            var source = PooledLootSourceText.Read(CreatureDeathPath);
            var onDeath = Code(PooledLootSourceText.MethodBody(source, "public virtual DeathMessage OnDeath(DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit = false)"));
            var die = Code(PooledLootSourceText.MethodBody(source, "protected virtual void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)"));

            const string Thread = "ACE.Server.ThreadDungeons.ThreadDungeonManager.OnRunCreatureDied(this, lastDamager, topDamager);";
            const string Trial = "if (P_TrialRep != null)\n                ACE.Server.BalanceTrials.TrialManager.OnTrialCreatureDied(this);";

            // XP is paid in OnDeath, and every creature-death caller runs OnDeath before Die (Monster_Combat, Creature_Vitals),
            // so a hook in Die runs after the award.
            StringAssert.Contains(onDeath, "OnDeath_GrantXP();", "OnDeath pays the kill");
            foreach (var caller in new[] { "Source/ACE.Server/WorldObjects/Monster_Combat.cs", "Source/ACE.Server/WorldObjects/Creature_Vitals.cs" })
                Assert.IsTrue(Regex.IsMatch(PooledLootSourceText.Read(caller), @"OnDeath\(DamageHistory\.LastDamager, [^;]*\);\s*Die\(\);"), $"{caller}: OnDeath then Die");

            var t = die.IndexOf(Thread, StringComparison.Ordinal);
            var b = die.IndexOf(Trial, StringComparison.Ordinal);

            Assert.IsTrue(t >= 0, "Threads hook");
            Assert.IsTrue(b >= 0, "trial hook");
            Assert.IsTrue(t < b, "the trial hook runs after the Threads hook");
            Assert.AreEqual(1, Regex.Matches(die, Regex.Escape("TrialManager.OnTrialCreatureDied(")).Count, "one report per death");
            Assert.AreEqual(0, Regex.Matches(onDeath, Regex.Escape("TrialManager.OnTrialCreatureDied(")).Count, "not in OnDeath");
        }
    }
}
