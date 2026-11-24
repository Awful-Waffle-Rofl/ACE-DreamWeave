using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Task 9, the death hold: the latch decision, the 1 Hz check (snap back, release, drop), the relog cases and the gate's
    /// refusal, all through <see cref="LiveGauntletDeathHooks"/> with a stepped clock and recorded effects. The run is a fake
    /// <see cref="IGauntletHoldRun"/>, except in the gate test, which drives the real GauntletRun and GauntletGate. A Player
    /// cannot run the death chain in this suite; the Player_Death.cs wiring is pinned by GauntletPlayerDeathHookSiteTests.
    /// Reads no PropertyManager key: the hold length is the run's tuning.
    /// </summary>
    [TestClass]
    public class GauntletDeathTests
    {
        private const uint Arena = 0x7F010001;
        private const uint OtherArena = 0x7F010002;
        private const uint FinishedArena = 0x7F010003;
        private const uint MemberGuid = 0x50000099;
        private const ushort Lb = GauntletFixtureSites.Primary;

        private static readonly DateTime T0 = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        private static uint nextGateGuid = 0x7ECC0000;

        private DateTime now;
        private LiveGauntletDeathHooks hooks;
        private RecordingEffects effects;
        private FakeHoldRun run;

        // ---- fakes -----------------------------------------------------------------------------------------------------

        private sealed class FakeHoldRun : IGauntletHoldRun
        {
            public uint RunKey { get; init; } = 7;
            public uint Instance { get; init; } = Arena;
            public bool IsTerminal { get; set; }
            public long DeathHoldSeconds { get; init; } = 60;
            public double CentreGx { get; init; } = OutdoorSiteGeometry.ToGlobalX(Lb, 100);
            public double CentreGy { get; init; } = OutdoorSiteGeometry.ToGlobalY(Lb, 96);
            public double Radius { get; init; } = 60;
            public Position PenPosition => At(100, 240, Instance);   // 144 m out: past the 135 m snap zone, as the lint requires
            public readonly List<Position> Entries = new List<Position> { At(110, 96, Arena), At(90, 96, Arena) };
            public readonly List<Player> Deaths = new List<Player>();
            public int DeathReports;
            private int rotation;

            public Position NextEntryPoint() => Entries[rotation++ % Entries.Count];

            public void OnMemberDied(Player player)
            {
                DeathReports++;
                Deaths.Add(player);
            }
        }

        private sealed class RecordingEffects : IGauntletHoldEffects
        {
            public readonly List<(Player Who, Position Pen, int Left)> Snaps = new List<(Player, Position, int)>();
            public readonly List<(Player Who, Position Entry)> Releases = new List<(Player, Position)>();

            public void SnapBack(Player player, Position pen, int secondsLeft) => Snaps.Add((player, pen, secondsLeft));
            public void Release(Player player, Position entry) => Releases.Add((player, entry));
        }

        private static Position At(float x, float y, uint instance)
            => new Position(GauntletFixtureSites.Cell(Lb), x, y, 50f, 0f, 0f, 0f, 1f, instance);

        private static Position InCircle => At(100, 96, Arena);
        private static Position InPen => At(100, 240, Arena);

        // ---- seeded players (UnsafeAccessor, never FieldInfo.SetValue on a Player) -----------------------------------------

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ephemeralPositions")]
        private static extern ref Dictionary<PositionType, Position> EphemeralPositions(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "positionCache")]
        private static extern ref Dictionary<PositionType, Position> PositionCache(WorldObject wo);

        private static Player Member(uint guid = MemberGuid)
        {
            var p = SalvageRollerVendorTests.MakePlayer(guid);
            EphemeralPositions(p) = new Dictionary<PositionType, Position>();
            PositionCache(p) = new Dictionary<PositionType, Position>();
            return p;
        }

        // ---- setup -------------------------------------------------------------------------------------------------------

        [TestInitialize]
        public void Setup()
        {
            now = T0;
            hooks = new LiveGauntletDeathHooks(() => now);
            effects = new RecordingEffects();
            hooks.Effects = effects;
            run = new FakeHoldRun();

            var finished = new FakeHoldRun { Instance = FinishedArena, IsTerminal = true };
            LiveGauntletDeathHooks.RunForInstance = i => i == Arena ? run : i == FinishedArena ? finished : null;
        }

        [TestCleanup]
        public void Cleanup()
        {
            LiveGauntletDeathHooks.ResetSeams();
            GauntletGate.ResetSeams();
        }

        private GauntletHoldStep Tick(Player p, Position at, bool busy = false) => hooks.TickPlayer(p, MemberGuid, at, busy);

        // ---- tests ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The latch is taken only for a death inside a live arena copy: never in the open world (instance 0), another
        /// instance, or the copy of a run that is already over. A latched death is counted on the run (OnMemberDied, with
        /// the dying player) and starts the run's hold length from the death moment.
        /// </summary>
        [TestMethod]
        public void Latch_SetOnlyInsideLiveCopy()
        {
            var p = Member();

            Assert.IsNull(hooks.LatchDeath(p, MemberGuid, 0), "the open world never latches");
            Assert.IsNull(hooks.LatchDeath(p, MemberGuid, OtherArena), "an instance with no Gauntlet run never latches");
            Assert.IsNull(hooks.LatchDeath(p, MemberGuid, FinishedArena), "a terminal run's copy never latches");
            Assert.AreEqual(0, hooks.Holds.Count, "no refused latch leaves a hold");
            Assert.AreEqual(0, run.DeathReports, "no refused latch is counted on the run");

            Assert.AreSame(run, hooks.LatchDeath(p, MemberGuid, Arena), "a death inside the live copy latches to its run");
            Assert.AreEqual(1, hooks.Holds.Count);
            Assert.AreEqual(1, run.DeathReports, "counted on the run once");
            Assert.AreSame(p, run.Deaths[0], "the run is told who died");
            Assert.AreEqual(60.0, hooks.Holds.SecondsLeft(run, MemberGuid), 1e-9, "the hold is the run's hold length, from the death moment");

            now = now.AddSeconds(20);
            Assert.AreEqual(40.0, hooks.Holds.SecondsLeft(run, MemberGuid), 1e-9, "the hold counts down on the clock");

            Assert.AreEqual(0.0, hooks.Holds.SecondsLeft(new FakeHoldRun(), MemberGuid), "a hold belongs to its own run only");
            Assert.AreEqual(0.0, hooks.Holds.SecondsLeft(run, MemberGuid + 1), "and to its own character only");
        }

        /// <summary>
        /// A held player outside the circle waits; at the hold's end the check releases them to the run's NEXT entry point
        /// (the destination the release effect receives), exactly once, and the hold is gone. The release's protection clear
        /// is pinned on a seeded Player here; its full vitals and its order are pinned on source by
        /// GauntletPlayerDeathHookSiteTests.Release_ClearsProtection_FillsVitals_ThenTeleports.
        /// </summary>
        [TestMethod]
        public void Release_AfterHold_FullVitals_ProtectionCleared_ToEntry()
        {
            var p = Member();
            hooks.LatchDeath(p, MemberGuid, Arena);

            now = now.AddSeconds(59);
            Assert.AreEqual(GauntletHoldStep.Wait, Tick(p, InPen), "one second left: still held");
            Assert.AreEqual(0, effects.Releases.Count);

            now = now.AddSeconds(1);
            Assert.AreEqual(GauntletHoldStep.Release, Tick(p, InPen), "the hold is over");
            Assert.AreEqual(1, effects.Releases.Count, "released once");
            Assert.AreSame(p, effects.Releases[0].Who);

            var entry = effects.Releases[0].Entry;
            Assert.IsNotNull(entry);
            Assert.AreEqual(Lb, (ushort)(entry.Cell >> 16), "on the arena landblock");
            Assert.AreEqual(110f, entry.PositionX, "the run's first entry point in rotation");
            Assert.AreEqual(96f, entry.PositionY);
            Assert.AreEqual(Arena, entry.Instance, "inside the arena copy");

            Assert.AreEqual(0, hooks.Holds.Count, "the hold ended");
            Assert.AreEqual(0.0, hooks.Holds.SecondsLeft(run, MemberGuid));
            Assert.AreEqual(GauntletHoldStep.None, Tick(p, InPen), "a released player is never released twice");
            Assert.AreEqual(1, effects.Releases.Count);
            Assert.AreEqual(0, effects.Snaps.Count, "the pen is outside the circle: never snapped");

            // The arrival shield: protected on arrival, ended by its own stamp 5 s later whatever the heartbeat.
            const double Arrive = 1_000_000.0;
            p.SetGauntletArrivalShield(Arrive);
            Assert.IsTrue(p.UnderLifestoneProtection, "a released player arrives protected");
            Assert.AreEqual(0.0, p.LifestoneProtectionTimestamp, "set as a full protection, so no heartbeat step can end it early");
            Assert.IsFalse(p.GauntletArrivalShieldExpiry(Arrive + 4.9), "still protected inside the 5 s");
            Assert.IsTrue(p.UnderLifestoneProtection);
            Assert.IsTrue(p.GauntletArrivalShieldExpiry(Arrive + 5.0), "ended at 5 s");
            Assert.IsFalse(p.UnderLifestoneProtection);
            Assert.IsNull(p.LifestoneProtectionTimestamp);
            Assert.IsFalse(p.GauntletArrivalShieldExpiry(Arrive + 6.0), "ended once");

            // An attack ended it early: the expiry ends nothing, and drops the stamp at once.
            p.SetGauntletArrivalShield(Arrive);
            p.UnderLifestoneProtection = false;
            Assert.IsFalse(p.GauntletArrivalShieldExpiry(Arrive + 1.0));
            p.UnderLifestoneProtection = true;   // a later protection the old stamp must not end
            p.LifestoneProtectionTimestamp = 0;
            Assert.IsFalse(p.GauntletArrivalShieldExpiry(Arrive + 5.0), "the dropped stamp ends nothing later");
            Assert.IsTrue(p.UnderLifestoneProtection);
        }

        [TestMethod]
        public void ArrivalShield_ALaterOrdinaryProtection_KeepsItsMinute()
        {
            const double Arrive = 1_000_000.0;
            var p = Member();

            p.SetGauntletArrivalShield(Arrive);
            p.SetLifestoneProtection();   // an ordinary death arrival inside the shield's seconds
            Assert.IsFalse(p.GauntletArrivalShieldExpiry(Arrive + 5.0), "the shield's stamp is gone");
            Assert.IsTrue(p.UnderLifestoneProtection, "the ordinary protection keeps its minute");
            Assert.AreEqual(0.0, p.LifestoneProtectionTimestamp);
        }

        [TestMethod]
        public void ArrivalShield_RelogMidShield_GetsAFresh5s_NotAMinute()
        {
            const double Login = 2_000_000.0;

            // A fresh Player carrying the persisted properties of a relog mid-shield: protected, timestamp 0, no stamp.
            var p = Member();
            p.UnderLifestoneProtection = true;
            p.LifestoneProtectionTimestamp = 0;

            p.GauntletLoginShieldCheck(Login, inGauntlet: true, heartbeatSeconds: 5);
            Assert.IsFalse(p.GauntletArrivalShieldExpiry(Login + 4.9));
            Assert.IsTrue(p.GauntletArrivalShieldExpiry(Login + 5.0), "ended 5 s after login, not 60");
            Assert.IsFalse(p.UnderLifestoneProtection);

            // Control: outside the Gauntlet, the same properties are an ordinary protection and are left alone.
            var outside = Member(MemberGuid + 1);
            outside.UnderLifestoneProtection = true;
            outside.LifestoneProtectionTimestamp = 0;
            outside.GauntletLoginShieldCheck(Login, inGauntlet: false, heartbeatSeconds: 5);
            Assert.IsFalse(outside.GauntletArrivalShieldExpiry(Login + 30));
            Assert.IsTrue(outside.UnderLifestoneProtection);

            // Control: a protection already part-way through (timestamp past 0) is left to the heartbeat.
            var older = Member(MemberGuid + 2);
            older.UnderLifestoneProtection = true;
            older.LifestoneProtectionTimestamp = 30;
            older.GauntletLoginShieldCheck(Login, inGauntlet: true, heartbeatSeconds: 5);
            Assert.IsFalse(older.GauntletArrivalShieldExpiry(Login + 30));
            Assert.IsTrue(older.UnderLifestoneProtection);

            // A relog after one or two heartbeats of the shield (each adds the 5 s interval) still reseeds.
            foreach (var stepped in new[] { 5.0, 10.0 })
            {
                var beat = Member(MemberGuid + 10 + (uint)stepped);
                beat.UnderLifestoneProtection = true;
                beat.LifestoneProtectionTimestamp = stepped;
                beat.GauntletLoginShieldCheck(Login, inGauntlet: true, heartbeatSeconds: 5);
                Assert.IsTrue(beat.GauntletArrivalShieldExpiry(Login + 5.0), $"timestamp {stepped}: reseeded to 5 s");
            }
            Assert.AreEqual(15.0, Player.GauntletRelogShieldTimestampBound(5));
            Assert.AreEqual(15.0, Player.GauntletRelogShieldTimestampBound(0), "an unticked heartbeat reads as the 5 s default");

            // Once per Player object.
            var once = Member(MemberGuid + 3);
            once.GauntletLoginShieldCheck(Login, inGauntlet: true, heartbeatSeconds: 5);
            once.UnderLifestoneProtection = true;
            once.LifestoneProtectionTimestamp = 0;
            once.GauntletLoginShieldCheck(Login + 100, inGauntlet: true, heartbeatSeconds: 5);
            Assert.IsFalse(once.GauntletArrivalShieldExpiry(Login + 200), "only the first check after login reseeds");
        }

        /// <summary>
        /// A held player standing inside the fight circle is sent back to the run's pen with the whole seconds left, and stays
        /// held. Outside the circle, outside the arena, or while busy (dying, teleporting, logging in) nothing happens.
        /// </summary>
        [TestMethod]
        public void Penned_WalksIntoCircle_SnappedBack()
        {
            var p = Member();
            hooks.LatchDeath(p, MemberGuid, Arena);

            now = now.AddSeconds(12.5);
            Assert.AreEqual(GauntletHoldStep.SnapBack, Tick(p, InCircle), "inside the circle with time left");
            Assert.AreEqual(1, effects.Snaps.Count);
            Assert.AreSame(p, effects.Snaps[0].Who);
            Assert.AreEqual(InPen.PositionX, effects.Snaps[0].Pen.PositionX);
            Assert.AreEqual(InPen.PositionY, effects.Snaps[0].Pen.PositionY);
            Assert.AreEqual(Arena, effects.Snaps[0].Pen.Instance, "to the pen inside the arena copy");
            Assert.AreEqual(48, effects.Snaps[0].Left, "the whole seconds left, rounded up");
            Assert.AreEqual(1, hooks.Holds.Count, "a snap keeps the hold");

            // The snap zone's edge: the 60 m circle plus 75 m. 134 m out is inside, 136 m out is not.
            Assert.AreEqual(GauntletHoldStep.SnapBack, Tick(p, At(161, 96, Arena)), "61 m out, past the circle but at its edge, is snapped back");
            Assert.AreEqual(GauntletHoldStep.SnapBack, Tick(p, At(234, 96, Arena)), "134 m from the centre is inside the snap zone");
            Assert.AreEqual(GauntletHoldStep.Wait, Tick(p, At(236, 96, Arena)), "136 m from the centre is outside it");
            Assert.AreEqual(GauntletHoldStep.Wait, Tick(p, InPen), "the pen itself is outside");
            Assert.AreEqual(GauntletHoldStep.Wait, Tick(p, At(100, 96, OtherArena)), "the same coordinates in another instance are not the arena");
            Assert.AreEqual(GauntletHoldStep.None, Tick(p, InCircle, busy: true), "a busy player is not checked");
            Assert.AreEqual(3, effects.Snaps.Count, "only the three checks inside the snap zone snapped");
            Assert.AreEqual(0, effects.Releases.Count, "nothing released while time is left");
        }

        /// <summary>
        /// Relog while penned: the hold is keyed by guid, so the Player object a login builds is checked like the old one. An
        /// expired hold releases at the new object's first check; a hold with time left keeps the player in the pen; a player
        /// whose login moved them out of the arena is dropped from the hold without being moved.
        /// </summary>
        [TestMethod]
        public void Relog_HoldExpired_ReleasedImmediately()
        {
            var before = Member();
            hooks.LatchDeath(before, MemberGuid, Arena);

            // Logged out 10 s into the hold, back 5 s later: still held, in the pen.
            now = now.AddSeconds(15);
            var relogged = Member();
            Assert.AreNotSame(before, relogged, "fixture: a login builds a new Player object");
            Assert.AreEqual(GauntletHoldStep.Wait, Tick(relogged, InPen), "time left: the relogged player stays penned");
            Assert.AreEqual(GauntletHoldStep.SnapBack, Tick(relogged, InCircle), "and is snapped back like before");

            // Logged out again, back after the hold ran out while offline: released at the first check.
            now = now.AddSeconds(120);
            var late = Member();
            Assert.AreEqual(GauntletHoldStep.Release, Tick(late, InPen), "expired while offline: released at once");
            Assert.AreEqual(1, effects.Releases.Count);
            Assert.AreSame(late, effects.Releases[0].Who, "the new Player object is the one released");
            Assert.AreEqual(Arena, effects.Releases[0].Entry.Instance);
            Assert.AreEqual(0, hooks.Holds.Count);

            // A second death; the login relocation moved the player out (for example, no longer a member): dropped, not moved.
            hooks.LatchDeath(late, MemberGuid, Arena);
            now = now.AddSeconds(61);
            var outside = Member();
            Assert.AreEqual(GauntletHoldStep.Drop, Tick(outside, At(100, 96, 0)), "expired outside the arena: forgotten");
            Assert.AreEqual(1, effects.Releases.Count, "nobody outside the arena is released into it");
            Assert.AreEqual(0, hooks.Holds.Count);

            // A hold whose run ended is forgotten too (the run's eviction moves the player).
            hooks.LatchDeath(outside, MemberGuid, Arena);
            run.IsTerminal = true;
            Assert.AreEqual(GauntletHoldStep.Drop, Tick(outside, InPen), "the run is over: dropped");
            Assert.AreEqual(1, effects.Releases.Count);
        }

        /// <summary>
        /// The gate (Task 10) reads the hold through the real run (GauntletRun.DeathHoldSecondsLeft -> the Death hook): a held
        /// member is refused with the whole seconds left, and let through once the hold is over. ReleaseAll ends every hold
        /// the run owns, and only those.
        /// </summary>
        [TestMethod]
        public void GateRefusesHeldPlayer_WithTimeLeft()
        {
            var told = new List<string>();
            var teleports = new List<Position>();
            GauntletGate.ResetSeams();
            GauntletGate.Told = (who, text) => told.Add(text);
            GauntletGate.Teleporter = (who, to) => teleports.Add(to);

            var env = new GauntletEnvironment
            {
                UtcNow = () => now,
                Hooks = new GauntletRunHooks { Death = hooks },
                MemberLookup = _ => _ => true,
            };
            var realRun = new GauntletRun(41, new GauntletStartRequest { AvatarId = 4100, NowUtc = T0 },
                GauntletFixtureSites.Site(GauntletFixtureSites.SixPortalRow()), 5, GauntletTuning.Defaults, env);
            realRun.Hold.State = GauntletState.Active;

            var member = Member();
            member.Location = new Position(0xA9B40019u, 84f, 96f, 20f, 0f, 0f, 0f, 1f, 0);

            var gate = new GenericObject(new Weenie
            {
                WeenieClassId = 1008505,
                WeenieType = WeenieType.Generic,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.AllegianceGauntletGate, true } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Gauntlet Gate" } },
            }, new ObjectGuid(++nextGateGuid));
            GauntletGate.Register(gate.Guid.Full, realRun);

            hooks.Holds.Begin(realRun, MemberGuid);
            Assert.AreEqual(60.0, realRun.DeathHoldSecondsLeft(member), 1e-9, "the run reads the installed hook");

            now = now.AddSeconds(15.2);
            Assert.IsTrue(GauntletGate.TryHandleUse(gate, member));
            CollectionAssert.AreEqual(new[] { "You have fallen in the arena. You may return in 45 seconds." }, told, "refused with the time left");
            Assert.AreEqual(0, teleports.Count, "a held member is not let in");

            // Another run's hold, and the release of this run's holds.
            var other = new FakeHoldRun();
            hooks.Holds.Begin(other, MemberGuid + 1);
            CollectionAssert.AreEqual(new[] { MemberGuid }, new List<uint>(hooks.Holds.ReleaseAll(realRun)), "ReleaseAll ends this run's holds");
            Assert.AreEqual(0.0, realRun.DeathHoldSecondsLeft(member), "released");
            Assert.AreEqual(1, hooks.Holds.Count, "another run's hold is untouched");

            // Past the hold, the gate goes on to its next check (the copy is not loaded here, so it answers "not ready").
            told.Clear();
            Assert.IsTrue(GauntletGate.TryHandleUse(gate, member));
            CollectionAssert.AreEqual(new[] { GauntletGate.NoEntryLine }, told, "no longer refused for the hold");

            // And an expired hold is not a refusal either.
            hooks.Holds.Begin(realRun, MemberGuid);
            now = now.AddSeconds(60);
            told.Clear();
            Assert.IsTrue(GauntletGate.TryHandleUse(gate, member));
            CollectionAssert.AreEqual(new[] { GauntletGate.NoEntryLine }, told, "an expired hold lets the member through");
        }

        /// <summary>The 1 Hz rule as a truth table.</summary>
        [TestMethod]
        public void Decide_TruthTable()
        {
            Assert.AreEqual(GauntletHoldStep.Drop, GauntletDeathHolds.Decide(runTerminal: true, inArena: true, insideCircle: true, expired: false));
            Assert.AreEqual(GauntletHoldStep.Release, GauntletDeathHolds.Decide(false, inArena: true, insideCircle: false, expired: true));
            Assert.AreEqual(GauntletHoldStep.Release, GauntletDeathHolds.Decide(false, inArena: true, insideCircle: true, expired: true), "an expired hold releases even from inside the circle");
            Assert.AreEqual(GauntletHoldStep.Drop, GauntletDeathHolds.Decide(false, inArena: false, insideCircle: false, expired: true));
            Assert.AreEqual(GauntletHoldStep.SnapBack, GauntletDeathHolds.Decide(false, inArena: true, insideCircle: true, expired: false));
            Assert.AreEqual(GauntletHoldStep.Wait, GauntletDeathHolds.Decide(false, inArena: true, insideCircle: false, expired: false));
            Assert.AreEqual(GauntletHoldStep.Wait, GauntletDeathHolds.Decide(false, inArena: false, insideCircle: false, expired: false));
        }

        /// <summary>The live install: LiveGauntletEnvironment.Build puts the one shared instance on GauntletRunHooks.Live.</summary>
        [TestMethod]
        public void LiveInstall_IsTheSharedInstance()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/AllegianceAvatar/Gauntlet/LiveGauntletWorld.cs");
            StringAssert.Contains(src, "if (hooks.Death is NoGauntletHooks)\n            hooks.Death = LiveGauntletDeathHooks.Instance;");

            var holds = PooledLootSourceText.Read("Source/ACE.Server/AllegianceAvatar/Gauntlet/GauntletDeathHold.cs");
            StringAssert.Contains(holds, "private static IGauntletHoldRun DefaultRunForInstance(uint instance) => GauntletManager.ForInstance(instance);",
                "the latch looks the run up through GauntletManager.ForInstance");
        }
    }
}
