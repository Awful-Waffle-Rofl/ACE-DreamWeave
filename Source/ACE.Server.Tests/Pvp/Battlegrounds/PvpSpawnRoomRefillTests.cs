using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Pvp;

using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// The respawn-zone refill (Docs/Pvp/TEMPLATES.md "Respawn zone"): the edge detector Player.TrackPvpSpawnRoomEntry decides
    /// WHEN the vitals refill, driven on seeded Players with real bindings and positions; the wiring (the tick hook, the binding
    /// carrying the room for the whole match, the refill using the vital-update path) is pinned on source.
    /// </summary>
    [TestClass]
    public class PvpSpawnRoomRefillTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        private const uint RoomCellA = 0x003C01D1u, RoomCellB = 0x003C0216u, OutsideCell = 0x003C01DDu;

        private static readonly SpawnRoomArea RoomOwn = new SpawnRoomArea(
            new HashSet<uint> { RoomCellA }, new[] { new SpawnRoomRect(55f, 75f, -85f, -75f) }, -7f, 0f);

        private static Position Pos(uint cell, float x, float y, float z) =>
            new Position { LandblockId = new LandblockId(cell), PositionX = x, PositionY = y, PositionZ = z, RotationW = 1f };

        private static readonly Position InOwnRoom = Pos(RoomCellA, 60f, -80f, -6f);
        private static readonly Position InEnemyRoom = Pos(RoomCellB, 170f, 0f, -6f);
        private static readonly Position Outside = Pos(OutsideCell, 80f, -80f, -6f);

        private Func<DateTime> savedClock;
        private Func<long> savedCooldown;
        private DateTime clockNow;

        [TestInitialize]
        public void Setup()
        {
            savedClock = PvpArenaHookSettings.UtcNow;
            savedCooldown = PvpArenaHookSettings.SpawnRefillCooldownSecondsSource;
            clockNow = T0;
            PvpArenaHookSettings.UtcNow = () => clockNow;
            PvpArenaHookSettings.SpawnRefillCooldownSecondsSource = () => 0;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpArenaHookSettings.UtcNow = savedClock;
            PvpArenaHookSettings.SpawnRefillCooldownSecondsSource = savedCooldown;
        }

        private static PvpMatch NewMatch() => new PvpMatch(Guid.NewGuid(), "bg_ad", new List<PvpTeam>
        {
            new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
            new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(3, 1500) })
        }, T0);

        private static PvpPlayerBinding Bind(PvpMatch m, int team, PvpMatchState state = PvpMatchState.Live, bool respawning = false, SpawnRoomArea room = null) =>
            new PvpPlayerBinding(m, team, state, true, true, true, true, true, respawning: respawning, spawnRoom: room ?? RoomOwn);

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }
        private static Player Seeded(PvpPlayerBinding binding, Position at)
        {
            var wo = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(wo, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(wo, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(wo, "positionCache", new Dictionary<PositionType, Position>());
            SetInherited(wo, "ephemeralPositions", new Dictionary<PositionType, Position>());

            wo.PlayerKillerStatus = PlayerKillerStatus.PK;
            wo.Location = new Position(at);
            wo.SetPvpBindingForTests(binding);

            return wo;
        }

        private static void MoveTo(Player p, Position at) => p.Location = new Position(at);

        [TestMethod]
        public void SteppingIntoYourOwnRoom_FiresOnce_NotEveryTick()
        {
            var p = Seeded(Bind(NewMatch(), 1), Outside);

            Assert.IsFalse(p.TrackPvpSpawnRoomEntry(), "outside: nothing");

            MoveTo(p, InOwnRoom);
            Assert.IsTrue(p.TrackPvpSpawnRoomEntry(), "the step that carries them in");
            Assert.IsFalse(p.TrackPvpSpawnRoomEntry(), "the next tick, still inside: no second refill");
            Assert.IsFalse(p.TrackPvpSpawnRoomEntry());

            MoveTo(p, Outside);
            Assert.IsFalse(p.TrackPvpSpawnRoomEntry());

            MoveTo(p, InOwnRoom);
            Assert.IsTrue(p.TrackPvpSpawnRoomEntry(), "leaving and coming back is a new entry");
        }

        [TestMethod]
        public void AnEnemyRoom_NeverRefills_BecauseTheBindingCarriesOnlyTheOwnRoom()
        {
            // Team 1's binding carries team 1's room (RoomOwn). Standing in the OTHER team's room is not "in the room".
            var p = Seeded(Bind(NewMatch(), 1), Outside);

            MoveTo(p, InEnemyRoom);
            Assert.IsFalse(p.TrackPvpSpawnRoomEntry());
            Assert.IsFalse(p.TrackPvpSpawnRoomEntry());
        }

        [TestMethod]
        public void NoRefillOutsideALiveMatch_OrWhileRespawning_OrWithNoBindingOrNoRoom()
        {
            var m = NewMatch();

            foreach (var state in new[] { PvpMatchState.Staging, PvpMatchState.Countdown, PvpMatchState.Closed })
            {
                var p = Seeded(Bind(m, 1, state), Outside);
                MoveTo(p, InOwnRoom);
                Assert.IsFalse(p.TrackPvpSpawnRoomEntry(), $"state {state}");
            }

            var dead = Seeded(Bind(m, 1, respawning: true), Outside);
            MoveTo(dead, InOwnRoom);
            Assert.IsFalse(dead.TrackPvpSpawnRoomEntry(), "a respawning player is in the pen flow, which already restores vitals");

            var none = Seeded(null, Outside);
            MoveTo(none, InOwnRoom);
            Assert.IsFalse(none.TrackPvpSpawnRoomEntry(), "no binding");

            var noRoom = Seeded(new PvpPlayerBinding(m, 1, PvpMatchState.Live, true, true, true, true, true), Outside);
            MoveTo(noRoom, InOwnRoom);
            Assert.IsFalse(noRoom.TrackPvpSpawnRoomEntry(), "a layout without rooms (King of the Hill)");
        }

        [TestMethod]
        public void TheFlagFollowsThePositionThroughANonLiveState_SoGoingLiveInsideDoesNotFire()
        {
            var m = NewMatch();
            var p = Seeded(Bind(m, 1, PvpMatchState.Countdown), Outside);

            MoveTo(p, InOwnRoom);
            Assert.IsFalse(p.TrackPvpSpawnRoomEntry(), "countdown: no refill");

            p.SetPvpBindingForTests(Bind(m, 1, PvpMatchState.Live));
            Assert.IsFalse(p.TrackPvpSpawnRoomEntry(), "already inside when the match went Live: not an entry");
        }

        // ================= wiring on source =================

        private static string Slice(string text, string startMarker)
        {
            var start = text.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.IsTrue(start > 0, startMarker + " was found");
            var next = Regex.Match(text.Substring(start + 10), @"(?m)^        (public|private|internal|protected)\b");
            return next.Success ? text.Substring(start, next.Index + 10) : text.Substring(start);
        }

        [TestMethod]
        public void TickHook_IsALiveStatement_NextToTheExitCheck_WithNoReturnBetweenTheCommitAndIt()
        {
            var body = Slice(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Tick.cs"), "public bool UpdatePlayerPosition(");
            var lines = body.Split('\n');

            var commit = Array.FindIndex(lines, l => l.Trim() == "Location = newPosition;");
            var exit = Array.FindIndex(lines, l => l.Trim() == "CheckPvpSpawnRoomExit();");
            var entry = Array.FindIndex(lines, l => l.Trim() == "CheckPvpSpawnRoomEntry();");

            Assert.IsTrue(commit >= 0, "the position commit is inside UpdatePlayerPosition");
            Assert.IsTrue(exit > commit, "the exit check runs after the commit");
            Assert.IsTrue(entry >= 0, "the entry check is a bare statement line (not `if (...) CheckPvpSpawnRoomEntry();`)");
            Assert.IsTrue(entry > exit && entry - exit <= 3, "within a few lines after the exit check");

            for (var i = exit + 1; i < entry; i++)
                Assert.IsTrue(lines[i].Trim().Length == 0 || lines[i].Trim().StartsWith("//"), $"only blank or comment lines between the two calls, found: {lines[i].Trim()}");

            Assert.AreEqual(lines[exit].Length - lines[exit].TrimStart().Length, lines[entry].Length - lines[entry].TrimStart().Length, "same block (same indent) as the exit check");

            for (var i = commit; i < entry; i++)
                Assert.IsFalse(Regex.IsMatch(lines[i], @"\breturn\b"), $"no return between the commit and the hook: {lines[i].Trim()}");
        }
        [TestMethod]
        public void TheBinding_CarriesTheOwnRoomForTheWholeMatch_NotOnlyWhileProtectedInRoom()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/Pvp/PvpMatchCoordinator.cs");
            var body = Slice(text, "private static SpawnRoomArea SeatRoom(");

            StringAssert.Contains(body, "SpawnRoomAreaFor(seat.TeamIndex)");
            Assert.IsFalse(body.Contains("ProtectedInRoom"), "gating the room on the protection flag drops it from the binding once protection ends, and the refill could never fire again");

            // Both Binding() constructors (templated and untemplated) must hand the room through.
            var binding = Slice(text, "private PvpPlayerBinding Binding(");
            Assert.AreEqual(2, Regex.Matches(binding, Regex.Escape("spawnRoom: SeatRoom(m, seat)")).Count, "both PvpPlayerBinding constructions carry the seat's room");
        }

        [TestMethod]
        public void TheEntryCheck_IsExactlyTheSeamCall_NotAGatedOrDisabledOne()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_PvpArena.cs");

            Assert.IsTrue(Regex.IsMatch(text, @"internal void CheckPvpSpawnRoomEntry\(\) => RunPvpSpawnRoomEntry\(RefillPvpSpawnRoomVitals\);"),
                "CheckPvpSpawnRoomEntry must be nothing but the seam call with the real refill");
        }

        // ================= the refill seam: a counting refill =================

        private static (Player Player, Func<bool> Refill, Func<int> Count) Counting(Position start, PvpMatchState state = PvpMatchState.Live)
        {
            var p = Seeded(Bind(NewMatch(), 1, state), start);
            var n = 0;
            return (p, () => { n++; return true; }, () => n);
        }

        [TestMethod]
        public void Seam_RefillsExactlyOncePerEntry_NeverWhileInside_AndAgainOnlyAfterAnExit()
        {
            var (p, refill, count) = Counting(Outside);

            Assert.IsFalse(p.RunPvpSpawnRoomEntry(refill));
            Assert.AreEqual(0, count());

            MoveTo(p, InOwnRoom);
            Assert.IsTrue(p.RunPvpSpawnRoomEntry(refill));
            Assert.AreEqual(1, count());

            for (var i = 0; i < 5; i++)
                Assert.IsFalse(p.RunPvpSpawnRoomEntry(refill), "inside: never");

            Assert.AreEqual(1, count());

            MoveTo(p, Outside);
            Assert.IsFalse(p.RunPvpSpawnRoomEntry(refill));
            Assert.AreEqual(1, count());

            MoveTo(p, InOwnRoom);
            Assert.IsTrue(p.RunPvpSpawnRoomEntry(refill));
            Assert.AreEqual(2, count(), "again only after an exit");
        }

        [TestMethod]
        public void Seam_NeverRefills_InAnEnemyRoom_OutsideLive_OrWithNoBinding()
        {
            var (enemy, refill1, count1) = Counting(Outside);
            MoveTo(enemy, InEnemyRoom);
            Assert.IsFalse(enemy.RunPvpSpawnRoomEntry(refill1));
            Assert.AreEqual(0, count1());

            var (staging, refill2, count2) = Counting(Outside, PvpMatchState.Staging);
            MoveTo(staging, InOwnRoom);
            Assert.IsFalse(staging.RunPvpSpawnRoomEntry(refill2));
            Assert.AreEqual(0, count2());

            var none = Seeded(null, Outside);
            var n = 0;
            MoveTo(none, InOwnRoom);
            Assert.IsFalse(none.RunPvpSpawnRoomEntry(() => { n++; return true; }));
            Assert.AreEqual(0, n);
        }

        [TestMethod]
        public void Seam_ARefillThatDidNotHappen_DoesNotStartTheCooldown()
        {
            PvpArenaHookSettings.SpawnRefillCooldownSecondsSource = () => 10;
            var p = Seeded(Bind(NewMatch(), 1), Outside);

            MoveTo(p, InOwnRoom);
            Assert.IsFalse(p.RunPvpSpawnRoomEntry(() => false), "declined (for example the player is dead)");

            MoveTo(p, Outside);
            p.RunPvpSpawnRoomEntry(() => true);
            MoveTo(p, InOwnRoom);
            Assert.IsTrue(p.RunPvpSpawnRoomEntry(() => true), "nothing was started by the declined one");
        }

        // ================= cooldown =================

        [TestMethod]
        public void Cooldown_TwoCrossingsInsideTheWindowRefillOnce_AfterTheWindowTwice()
        {
            PvpArenaHookSettings.SpawnRefillCooldownSecondsSource = () => 10;
            var (p, refill, count) = Counting(Outside);

            MoveTo(p, InOwnRoom);
            Assert.IsTrue(p.RunPvpSpawnRoomEntry(refill));
            MoveTo(p, Outside);
            p.RunPvpSpawnRoomEntry(refill);

            clockNow = T0.AddSeconds(9.9);
            MoveTo(p, InOwnRoom);
            Assert.IsFalse(p.RunPvpSpawnRoomEntry(refill), "second crossing inside the window");
            Assert.AreEqual(1, count());

            MoveTo(p, Outside);
            p.RunPvpSpawnRoomEntry(refill);

            clockNow = T0.AddSeconds(10.0);
            MoveTo(p, InOwnRoom);
            Assert.IsTrue(p.RunPvpSpawnRoomEntry(refill), "after the window");
            Assert.AreEqual(2, count());
        }

        [TestMethod]
        public void Cooldown_ZeroMeansNoCooldown_AndASkippedEntryIsNotRetriedWhileStillInside()
        {
            var (p, refill, count) = Counting(Outside);

            for (var i = 0; i < 3; i++)
            {
                MoveTo(p, InOwnRoom);
                Assert.IsTrue(p.RunPvpSpawnRoomEntry(refill));
                MoveTo(p, Outside);
                p.RunPvpSpawnRoomEntry(refill);
            }

            Assert.AreEqual(3, count(), "cooldown 0: every entry");

            PvpArenaHookSettings.SpawnRefillCooldownSecondsSource = () => 10;
            MoveTo(p, InOwnRoom);
            Assert.IsFalse(p.RunPvpSpawnRoomEntry(refill), "inside the window of the last refill");

            clockNow = T0.AddSeconds(60);
            Assert.IsFalse(p.RunPvpSpawnRoomEntry(refill), "still inside the room: the skipped entry is not retried when the window lapses");
            Assert.AreEqual(3, count());
        }

        [TestMethod]
        public void Cooldown_SettingIsWiredInAllThreeRegistries_AndTheSeamReadsIt()
        {
            var props = PooledLootSourceText.Read("Source/ACE.Server/Managers/PropertyManager.cs");
            StringAssert.Contains(props, "(\"pvp_bg_spawn_refill_cooldown_seconds\", new Property<long>(10,");

            var hook = PooledLootSourceText.Read("Source/ACE.Server/Pvp/PvpArenaHookSettings.cs");
            StringAssert.Contains(hook, "PropertyManager.GetLong(\"pvp_bg_spawn_refill_cooldown_seconds\"");

            var arena = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_PvpArena.cs");
            StringAssert.Contains(arena, "PvpArenaHookSettings.SpawnRefillCooldownSecondsSource()");
        }
        [TestMethod]
        public void TheRefill_UsesTheVitalUpdatePath_AndSkipsTheDead()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_PvpArena.cs");
            var body = Slice(text, "private bool RefillPvpSpawnRoomVitals(");

            StringAssert.Contains(body, "RestoreRespawnVitals(");
            StringAssert.Contains(body, "UpdateVital(");
            StringAssert.Contains(body, "IsDead");
            StringAssert.Contains(body, "IsInDeathProcess");
        }
    }
}