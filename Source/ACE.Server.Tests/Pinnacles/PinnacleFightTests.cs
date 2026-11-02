using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using ACE.Server.HazardMechanics;
using ACE.Server.Knots;
using ACE.Server.Pinnacles;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// PinnacleFight lifecycle against FakePinnacleWorld (Docs/Knots/PINNACLE-ENCOUNTER-PLAN.md M2b). No
    /// PropertyManager key is read by the core or these fakes, so nothing needs seeding.
    /// </summary>
    [TestClass]
    public class PinnacleFightTests
    {
        private const uint HeadWcid = 900;
        private const uint SealWcid = 800;
        private const uint AddWcid = 500;
        private const uint OtherAddWcid = 501;

        private static readonly PinnaclePoint P = new PinnaclePoint(0x00690113, 10f, -20f, -47f);

        /// <summary>A script made of delegates that records every callback.</summary>
        private sealed class TestScript : IPinnacleBossScript
        {
            public string Kind => "test";

            public PinnacleScriptData Data { get; set; } = NewData(PinnacleTier.Boss);

            public static PinnacleScriptData NewData(PinnacleTier headTier) => new PinnacleScriptData
            {
                Landblock = 0x0069,
                SealWcid = SealWcid,
                SealPoints = new[] { new PinnaclePoint(0x00690113, 34f, -18f, -47f), new PinnaclePoint(0x00690113, 34f, -22f, -47f) },
                HeadWcid = HeadWcid,
                HeadPoint = new PinnaclePoint(0x00690113, 10.6544f, -20.161f, -47.1039f),
                HazardRegionPreset = "pinnacle",
                HeadTier = headTier,
            };

            public int PhaseCount { get; set; } = 5;

            public List<string> Log { get; } = new List<string>();

            public List<PinnacleEvent> Events { get; } = new List<PinnacleEvent>();

            public Action<IPinnacleFightPort> Started;
            public Action<int, IPinnacleFightPort> Entered;
            public Action<PinnacleEvent, IPinnacleFightPort> Evented;
            public Action<IPinnacleFightPort> Restarted;

            public void OnStart(IPinnacleFightPort port)
            {
                Log.Add("OnStart");
                Started?.Invoke(port);
            }

            public void EnterPhase(int phase, IPinnacleFightPort port)
            {
                Log.Add($"Enter:{phase}");
                Entered?.Invoke(phase, port);
            }

            public void OnEvent(PinnacleEvent e, IPinnacleFightPort port)
            {
                Log.Add($"Event:{e}");
                Events.Add(e);
                Evented?.Invoke(e, port);
            }

            public void RestartPhase(IPinnacleFightPort port)
            {
                Log.Add($"Restart:{port.Phase}");
                Restarted?.Invoke(port);
            }

            public string Describe() => "test script";
        }

        private static PinnacleFightConfig Cfg => new PinnacleFightConfig { HoldSeconds = 600, ResumeGraceSeconds = 5 };

        private static PinnacleFight NewFight(FakePinnacleWorld world, TestScript script, PinnacleFightConfig cfg = null)
            => new PinnacleFight(script, world, cfg ?? Cfg, ownerGuid: 0x50000001, knotGuid: 0x80000002, instance: 7, knotLevel: 250, seed: 42);

        /// <summary>First tick (head spawns), then the head talk at t=0.</summary>
        private static PinnacleFight Begun(FakePinnacleWorld world, TestScript script, PinnacleFightConfig cfg = null)
        {
            var fight = NewFight(world, script, cfg);
            fight.Tick(0);
            Assert.IsTrue(fight.TryBegin(0));
            return fight;
        }

        /// <summary>Holds at <paramref name="at"/> via a death; the owner is then away.</summary>
        private static void HoldAt(PinnacleFight fight, FakePinnacleWorld world, double at)
        {
            fight.OwnerDied(at);
            world.OwnerPresent = false;
            Assert.AreEqual(PinnacleFightState.Held, fight.State);
        }

        /// <summary>Arms, returns the owner at <paramref name="at"/>, and ticks through the grace.</summary>
        private static void ResumeAt(PinnacleFight fight, FakePinnacleWorld world, double at)
        {
            fight.ArmResume();
            world.OwnerPresent = true;
            fight.Tick(at);
            fight.Tick(at + 5);
            Assert.AreEqual(PinnacleFightState.Running, fight.State, "resumed after the grace");
        }

        // ================================================================ begin

        [TestMethod]
        public void TryBegin_SealsChargesStartsAndEntersPhase1()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript();
            int chargesAtStart = -1;
            script.Started = _ => chargesAtStart = world.Charges;

            var fight = NewFight(world, script);
            fight.Tick(0);

            Assert.AreEqual(1, world.Spawns.Count(s => s.Wcid == HeadWcid), "the head spawns on the first tick");
            Assert.AreEqual(PinnacleFightState.NotStarted, fight.State);
            Assert.AreEqual(0, world.Charges);

            Assert.IsTrue(fight.TryBegin(1));

            Assert.AreEqual(2, world.Spawns.Count(s => s.Wcid == SealWcid), "one ward per seal point");
            Assert.IsTrue(world.Spawns.Where(s => s.Wcid == SealWcid).All(s => !s.WithImmuneRecord));
            Assert.AreEqual(1, world.Charges);
            Assert.AreEqual(1, chargesAtStart, "charged before OnStart");
            CollectionAssert.AreEqual(new[] { "OnStart", "Enter:1" }, script.Log);
            Assert.AreEqual(PinnacleFightState.Running, fight.State);
            Assert.AreEqual(1, fight.Phase);
            Assert.AreEqual(world.HandleOf(HeadWcid), fight.Head);
            Assert.AreEqual(250, fight.KnotLevel);

            var lastSeal = world.Calls.LastIndexOf("Spawn");
            Assert.IsTrue(lastSeal < world.Calls.IndexOf("ChargeEntry"), "sealed before charging");
        }

        [TestMethod]
        public void Head_SpawnsAtTheScriptHeadTier_AddsAtTheirOwnTier()
        {
            // E2: the head takes PinnacleScriptData.HeadTier; each add the tier its script passed, on the retry too
            var world = new FakePinnacleWorld();
            var script = new TestScript { Data = TestScript.NewData(PinnacleTier.Head) };
            script.Entered = (n, port) =>
            {
                if (n == 1)
                {
                    port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite);
                    world.FailNextSpawns = 1;
                    port.SpawnAdd("grael", OtherAddWcid, P, PinnacleTier.Boss);
                }
            };
            Begun(world, script);

            Assert.AreEqual(PinnacleTier.Head, world.Spawns.Single(s => s.Wcid == HeadWcid).Tier);
            Assert.AreEqual(PinnacleTier.Elite, world.Spawns.Single(s => s.Wcid == AddWcid).Tier);

            var boss = world.Spawns.Where(s => s.Wcid == OtherAddWcid).ToList();
            Assert.AreEqual(2, boss.Count, "failed once, retried once");
            Assert.IsTrue(boss.All(s => s.Tier == PinnacleTier.Boss), "the retry keeps the tier");
        }

        [TestMethod]
        public void DispelOwnerBuffs_ZeroBuffs_StillCallsTheWorld()
        {
            // a failed round shows the visual and the nothing line, so the world is reached even with nothing to take
            var world = new FakePinnacleWorld { BuffsAvailable = 0 };
            var fight = NewFight(world, new TestScript());

            Assert.AreEqual(0, fight.DispelOwnerBuffs(5));
            Assert.AreEqual(0, fight.DispelOwnerBuffs(0));
            CollectionAssert.AreEqual(new[] { 5, 0 }, world.DispelRequests);
        }

        [TestMethod]
        public void Grael_P1RunsTheWispsWithTheBody_AndTheBodysDeathEndsBoth_StraightIntoTheMemoryPhase()
        {
            // round 4 playtest: P2's wisps run during P1; the retired P2 passes straight on to P3 (real script, fake world)
            var world = new FakePinnacleWorld();
            var fight = new PinnacleFight(new ACE.Server.Pinnacles.Grael.GraelScript(), world, Cfg, ownerGuid: 0x50000001, knotGuid: 0x80000002, instance: 7, knotLevel: 250, seed: 42);
            fight.Tick(0);
            Assert.IsTrue(fight.TryBegin(0));

            Assert.AreEqual(1, fight.Phase);
            var wisps = world.Hazards.Single();
            Assert.AreEqual(HazardMechanicRegistry.Wisps, wisps.Kind);
            Assert.AreEqual(1, world.ActiveHazardCount);

            // the wisp series completing leaves the fight in P1 with a fresh run
            fight.OnHazardEnded(wisps.Token, HazardOutcome.Completed);
            fight.Tick(1);
            Assert.AreEqual(1, fight.Phase);
            Assert.AreEqual(2, world.Hazards.Count(h => h.Kind == HazardMechanicRegistry.Wisps), "a fresh wisp run");

            world.Kill(world.HandleOf(ACE.Server.Pinnacles.Grael.GraelData.GraelWcid));
            fight.Tick(2);

            Assert.AreEqual(3, fight.Phase, "P2 is passed through in the same tick");
            Assert.IsTrue(world.StoppedTokens.Contains(world.Hazards.Last(h => h.Kind == HazardMechanicRegistry.Wisps).Token), "the body's death stopped the live wisp run");
            Assert.AreEqual(2, world.ActiveHazardCount, "the fake counts the completed first wisp run (it ended, so nothing stops it) and the memory run");
            Assert.AreEqual(HazardMechanicRegistry.Memory, world.Hazards.Last().Kind);
            Assert.AreEqual(PinnacleFightState.Running, fight.State);
        }

        [TestMethod]
        public void Grael_P1HoldAndResume_AfterACompletedWispRun_OneLiveFlatRun_TheBodyUntouched()
        {
            var world = new FakePinnacleWorld();
            var fight = new PinnacleFight(new ACE.Server.Pinnacles.Grael.GraelScript(), world, Cfg, ownerGuid: 0x50000001, knotGuid: 0x80000002, instance: 7, knotLevel: 250, seed: 42);
            fight.Tick(0);
            Assert.IsTrue(fight.TryBegin(0));

            var body = world.HandleOf(ACE.Server.Pinnacles.Grael.GraelData.GraelWcid);
            var completed = world.Hazards.Single().Token;
            Assert.AreNotEqual(world.Hazards.Single().Args.GetString("move"), world.Hazards.Single().Args.GetString("move_end"), "the first run ramps");

            fight.OnHazardEnded(completed, HazardOutcome.Completed);
            fight.Tick(1);
            Assert.AreEqual(2, world.Hazards.Count);

            HoldAt(fight, world, 2);
            ResumeAt(fight, world, 3);

            Assert.AreEqual(1, fight.Phase);

            // the completed run ended by itself (the fake never marks it stopped); every other run but the last was stopped
            var live = world.Hazards.Where(h => h.Token > 0 && h.Token != completed && !world.StoppedTokens.Contains(h.Token)).ToList();
            Assert.AreEqual(1, live.Count, "exactly one live wisp run after the resume");
            Assert.AreEqual(HazardMechanicRegistry.Wisps, live[0].Kind);
            Assert.AreEqual(world.Hazards.Last().Token, live[0].Token, "the resume's restart");
            Assert.AreEqual(live[0].Args.GetString("move_end"), live[0].Args.GetString("move"), "the restarted run holds the hardest values flat");
            Assert.AreEqual("1.5", live[0].Args.GetString("move"));

            Assert.IsTrue(world.IsAlive(body), "the Grael body still stands");
            Assert.IsFalse(world.Destroyed.Contains(body));
            Assert.AreEqual(1, world.Spawns.Count(s => s.Wcid == ACE.Server.Pinnacles.Grael.GraelData.GraelWcid), "and is never respawned");
        }

        [TestMethod]
        public void Grael_HeadCastsFromTheStart_StopsWhileHeld_AndAfterTheWin()
        {
            // round 4 playtest: the head casts in P1 every EarlyCastSeconds; a hold pauses it, a win ends it
            var world = new FakePinnacleWorld();
            var fight = new PinnacleFight(new ACE.Server.Pinnacles.Grael.GraelScript(), world, Cfg, ownerGuid: 0x50000001, knotGuid: 0x80000002, instance: 7, knotLevel: 250, seed: 42);
            fight.Tick(0);
            Assert.IsTrue(fight.TryBegin(0));
            var head = fight.Head;
            var every = ACE.Server.Pinnacles.Grael.GraelScript.EarlyCastSeconds;

            Assert.AreEqual(1, fight.Phase);
            Assert.IsFalse(world.Attackable.TryGetValue(head, out var a) && a, "the head is not attackable in P1");

            fight.Tick(every - 0.5);
            Assert.AreEqual(0, world.Casts.Count, "not before the first interval");
            fight.Tick(every + 0.1);
            fight.Tick(every + 0.2);
            Assert.AreEqual(1, world.Casts.Count, "the first P1 cast");
            Assert.AreEqual(head, world.Casts[0].Caster);

            // held: no cast however long the hold lasts
            HoldAt(fight, world, every + 1);
            var heldCasts = world.Casts.Count;
            for (var t = every + 2; t < every + 60; t += 1)
                fight.Tick(t);
            Assert.AreEqual(heldCasts, world.Casts.Count, "no cast while held");

            // resumed: casting carries on
            ResumeAt(fight, world, every + 61);
            for (var t = every + 67; t < every + 67 + 3 * every; t += 0.5)
                fight.Tick(t);
            Assert.IsTrue(world.Casts.Count > heldCasts, "casting resumes after the hold");

            // win: the head dies, then nothing more is cast
            world.Kill(head);
            var now = every + 67 + 3 * every;
            fight.Tick(now);
            Assert.AreEqual(PinnacleFightState.Won, fight.State);
            var wonCasts = world.Casts.Count;
            for (var t = now + 1; t < now + 60; t += 1)
                fight.Tick(t);
            Assert.AreEqual(wonCasts, world.Casts.Count, "no cast after the win");
        }

        [TestMethod]
        public void Grael_AdminJumpToTheRetiredP2_LandsInP3()
        {
            var world = new FakePinnacleWorld();
            var fight = new PinnacleFight(new ACE.Server.Pinnacles.Grael.GraelScript(), world, Cfg, ownerGuid: 0x50000001, knotGuid: 0x80000002, instance: 7, knotLevel: 250, seed: 42);
            fight.Tick(0);
            Assert.IsTrue(fight.TryBegin(0));

            fight.JumpToPhase(2, 1);

            Assert.AreEqual(3, fight.Phase);
            Assert.AreEqual(1, world.ActiveHazardCount, "only the memory run is live");
            Assert.AreEqual(HazardMechanicRegistry.Memory, world.Hazards.Last().Kind);
        }

        [TestMethod]
        public void Head_DefaultsToBossTier()
        {
            Assert.AreEqual(PinnacleTier.Boss, new PinnacleScriptData().HeadTier);

            var world = new FakePinnacleWorld();
            var fight = NewFight(world, new TestScript { Data = new PinnacleScriptData { HeadWcid = HeadWcid, HeadPoint = P } });
            fight.Tick(0);

            Assert.AreEqual(PinnacleTier.Boss, world.Spawns.Single(s => s.Wcid == HeadWcid).Tier);
        }

        [TestMethod]
        public void Level_IsTheKnotLevel()
        {
            var fight = NewFight(new FakePinnacleWorld(), new TestScript());

            Assert.AreEqual(250, ((IPinnacleFightPort)fight).Level);
            Assert.AreEqual(fight.KnotLevel, ((IPinnacleFightPort)fight).Level);
        }

        [TestMethod]
        public void TryBegin_SecondCall_IsNoOp()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript();
            var fight = Begun(world, script);
            var calls = world.Calls.Count;

            Assert.IsFalse(fight.TryBegin(2));
            Assert.IsFalse(fight.TryBegin(3, charge: false));

            Assert.AreEqual(1, world.Charges);
            Assert.AreEqual(calls, world.Calls.Count, "no world call at all");
            CollectionAssert.AreEqual(new[] { "OnStart", "Enter:1" }, script.Log);
        }

        [TestMethod]
        public void NotStarted_OwnerAbsent_NoHold()
        {
            var world = new FakePinnacleWorld { OwnerPresent = false };
            var script = new TestScript();
            var fight = NewFight(world, script);

            fight.Tick(0);
            fight.OwnerDied(1);
            for (var t = 2; t <= 1300; t += 50)
                fight.Tick(t);

            Assert.AreEqual(PinnacleFightState.NotStarted, fight.State);
            Assert.IsFalse(fight.IsEnded);
            Assert.IsNull(fight.UnloadIntervalOverride, "an unstarted copy unloads normally");
            Assert.AreEqual(0, world.AttemptLostReports);
            Assert.AreEqual(0, world.Releases.Count);
            Assert.AreEqual(1, world.Spawns.Count(s => s.Wcid == HeadWcid), "head spawned once and kept");
        }

        // ================================================================ hold

        [TestMethod]
        public void OwnerDied_Holds_StopsHazards_FreezesClock()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript { Entered = (n, port) => port.StartHazard("h", "wisps", "scatter", HazardArgs.Empty) };
            var fight = Begun(world, script);
            var token = world.Hazards.Single().Token;

            fight.Tick(10);
            Assert.AreEqual(10, fight.Now, 1e-9);

            fight.OwnerDied(10);
            world.OwnerPresent = false;

            Assert.AreEqual(PinnacleFightState.Held, fight.State);
            CollectionAssert.Contains(world.StoppedTokens, token);

            fight.Tick(20);
            fight.Tick(300);
            Assert.AreEqual(10, fight.Now, 1e-9, "fight clock frozen while held");
            Assert.AreEqual(1, world.Hazards.Count, "nothing restarted while held");
        }

        [TestMethod]
        public void OwnerAbsentOnTick_Holds()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript();
            var fight = Begun(world, script);

            fight.Tick(3);
            world.OwnerPresent = false;   // logout, recall, portal
            fight.Tick(5);

            Assert.AreEqual(PinnacleFightState.Held, fight.State);
            Assert.AreEqual(3, fight.Now, 1e-9, "the absent tick does not advance the clock");
            Assert.IsNotNull(fight.UnloadIntervalOverride);
        }

        [TestMethod]
        public void Held_ExpiresAtHoldSeconds_EndsLost_CleanupOnce()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript { Entered = (n, port) => port.SpawnAdd("grael", AddWcid, P, PinnacleTier.Elite) };
            var fight = Begun(world, script);
            HoldAt(fight, world, 10);

            fight.Tick(609.9);
            Assert.AreEqual(PinnacleFightState.Held, fight.State);

            fight.Tick(610);
            Assert.AreEqual(PinnacleFightState.Lost, fight.State);
            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(1, world.AttemptLostReports);
            CollectionAssert.AreEqual(new[] { false }, world.Releases, "released once; owner not inside");

            foreach (var s in world.Spawns.Where(s => !s.Result.IsNone))
                Assert.IsFalse(world.IsAlive(s.Result), $"wcid {s.Wcid} cleaned up");

            var calls = world.Calls.Count;
            fight.Tick(611);
            fight.Tick(5000);
            fight.Reset("again");
            fight.OwnerDied(5001);
            Assert.AreEqual(calls, world.Calls.Count, "cleanup ran exactly once");
            Assert.AreEqual(1, world.AttemptLostReports);
        }

        [TestMethod]
        public void Held_HealthNeverRises()
        {
            var world = new FakePinnacleWorld { DefaultHealth = 1000 };
            PinnacleHandle add = default;
            var script = new TestScript { Entered = (n, port) => add = port.SpawnAdd("grael", AddWcid, P, PinnacleTier.Elite) };
            var fight = Begun(world, script);
            world.Health[add] = 800;
            HoldAt(fight, world, 10);

            world.Health[add] = 950;            // regen
            world.Health[fight.Head] = 1200;
            fight.Tick(11);
            Assert.AreEqual(800, world.Health[add]);
            Assert.AreEqual(1000, world.Health[fight.Head]);

            world.Health[add] = 600;            // a DoT still lands
            fight.Tick(12);
            Assert.AreEqual(600, world.Health[add]);

            world.Health[add] = 700;            // and must not be regenerated back
            fight.Tick(13);
            Assert.AreEqual(600, world.Health[add]);
        }

        [TestMethod]
        public void Held_ArmedAndPresent_ResumesOnlyAfterGrace()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript();
            var fight = Begun(world, script);
            HoldAt(fight, world, 10);

            world.OwnerPresent = true;           // present but never armed
            fight.Tick(20);
            fight.Tick(40);
            Assert.AreEqual(PinnacleFightState.Held, fight.State, "presence alone never resumes");

            fight.ArmResume();
            fight.Tick(41);                      // present since 41
            fight.Tick(45.9);
            Assert.AreEqual(PinnacleFightState.Held, fight.State, "inside the grace");

            world.OwnerPresent = false;          // left again: the grace restarts
            fight.Tick(46);
            world.OwnerPresent = true;
            fight.Tick(47);
            fight.Tick(51.9);
            Assert.AreEqual(PinnacleFightState.Held, fight.State);

            fight.Tick(52);
            Assert.AreEqual(PinnacleFightState.Running, fight.State);
        }

        [TestMethod]
        public void Held_GraceOver_StillInPortalSpace_WaitsForArrival()
        {
            // prod 2026-10-30: the acid resumed ~1 s before the returning player could move, and killed them as they arrived
            var world = new FakePinnacleWorld();
            var script = new TestScript();
            var fight = Begun(world, script);
            HoldAt(fight, world, 10);

            fight.ArmResume();
            world.OwnerPresent = true;
            world.OwnerInPortalSpace = true;
            fight.Tick(20);                      // present since 20
            fight.Tick(40);
            Assert.AreEqual(PinnacleFightState.Held, fight.State, "the grace is over but the owner cannot move yet");

            world.OwnerInPortalSpace = false;
            fight.Tick(41);
            Assert.AreEqual(PinnacleFightState.Running, fight.State, "arrival resumes at once: the grace already ran in portal space");
        }

        [TestMethod]
        public void ArmResume_FromAnotherThread_Resumes()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript();
            var fight = Begun(world, script);
            HoldAt(fight, world, 10);

            Task.Run(() => fight.ArmResume()).Wait();
            world.OwnerPresent = true;
            fight.Tick(20);
            fight.Tick(25);

            Assert.AreEqual(PinnacleFightState.Running, fight.State);
        }

        [TestMethod]
        public void Hold_ClearsAnEarlierArm()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript();
            var fight = Begun(world, script);

            fight.ArmResume();                   // stale arm from before the hold
            HoldAt(fight, world, 10);
            world.OwnerPresent = true;
            fight.Tick(20);
            fight.Tick(30);

            Assert.AreEqual(PinnacleFightState.Held, fight.State);
        }

        [TestMethod]
        public void Resume_CallsRestartPhase()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript();
            var fight = Begun(world, script);
            HoldAt(fight, world, 10);

            ResumeAt(fight, world, 20);

            CollectionAssert.AreEqual(new[] { "OnStart", "Enter:1", "Restart:1" }, script.Log);
            Assert.AreEqual(1, fight.Phase);
        }

        [TestMethod]
        public void AdvanceRequestedWhileHeld_ResumeEntersNextPhase()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript();
            var fight = Begun(world, script);
            HoldAt(fight, world, 10);

            ((IPinnacleFightPort)fight).AdvancePhase();
            fight.Tick(11);
            Assert.AreEqual(1, fight.Phase, "not applied while held");

            ResumeAt(fight, world, 20);

            Assert.AreEqual(2, fight.Phase);
            CollectionAssert.AreEqual(new[] { "OnStart", "Enter:1", "Enter:2" }, script.Log, "Enter(next) instead of RestartPhase");
        }

        [TestMethod]
        public void AddKilledDuringHold_DeliveredAtResume()
        {
            var world = new FakePinnacleWorld();
            PinnacleHandle grael = default;
            var script = new TestScript
            {
                Entered = (n, port) => { if (n == 1) grael = port.SpawnAdd("grael", AddWcid, P, PinnacleTier.Elite); },
                Evented = (e, port) => { if (e.Kind == PinnacleEventKind.AddDied && e.Tag == "grael") port.AdvancePhase(); },
            };
            var fight = Begun(world, script);
            HoldAt(fight, world, 10);

            world.Kill(grael);                   // a DoT finishes Grael during the hold
            fight.Tick(11);
            fight.Tick(12);
            Assert.AreEqual(0, script.Events.Count, "nothing is delivered while held");

            ResumeAt(fight, world, 20);

            Assert.AreEqual(1, script.Events.Count);
            Assert.AreEqual(grael, script.Events[0].Handle);
            Assert.AreEqual(2, fight.Phase, "the kill stands");
            Assert.IsFalse(script.Log.Any(l => l.StartsWith("Restart")), "no RestartPhase when the phase advanced");
        }

        // ================================================================ hazards

        [TestMethod]
        public void StopHazardByTag_StopsOnlyThatTag_AndItsLateEndIsIgnored()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript
            {
                Entered = (n, port) =>
                {
                    if (n == 1)
                    {
                        port.StartHazard("corners", "zones", "corners", HazardArgs.Empty);
                        port.StartHazard("fan", "balls", "fan", HazardArgs.Empty);
                    }
                },
            };
            var fight = Begun(world, script);
            var corners = world.Hazards[0].Token;
            var fan = world.Hazards[1].Token;

            ((IPinnacleFightPort)fight).StopHazard("corners");

            CollectionAssert.AreEqual(new[] { corners }, world.StoppedTokens, "only the tagged run is stopped");

            fight.OnHazardEnded(corners, HazardOutcome.Stopped);
            fight.OnHazardEnded(fan, HazardOutcome.Completed);
            fight.Tick(1);

            Assert.AreEqual(1, script.Events.Count, "the stopped run's end is never delivered");
            Assert.AreEqual("fan", script.Events[0].Tag);

            // an unknown tag is a no-op
            ((IPinnacleFightPort)fight).StopHazard("nothing");
            Assert.AreEqual(1, world.StoppedTokens.Count);
        }

        [TestMethod]
        public void StopHazardByTag_DropsAPendingRetry()
        {
            var world = new FakePinnacleWorld { RefuseNextHazards = 1 };
            var script = new TestScript { Entered = (n, port) => { if (n == 1) port.StartHazard("corners", "zones", "corners", HazardArgs.Empty); } };
            var fight = Begun(world, script);
            var attempts = world.Hazards.Count;

            ((IPinnacleFightPort)fight).StopHazard("corners");
            fight.Tick(1);
            fight.Tick(2);

            Assert.AreEqual(attempts, world.Hazards.Count, "the refused run is not retried once stopped");
            Assert.AreEqual(0, script.Events.Count);

            // control: the same refusal without the stop IS retried, so the assertion above discriminates
            var world2 = new FakePinnacleWorld { RefuseNextHazards = 1 };
            var fight2 = Begun(world2, new TestScript { Entered = (n, port) => { if (n == 1) port.StartHazard("corners", "zones", "corners", HazardArgs.Empty); } });
            var before = world2.Hazards.Count;
            fight2.Tick(1);
            fight2.Tick(2);
            Assert.IsTrue(world2.Hazards.Count > before, "control: retried");
        }

        [TestMethod]
        public void CoreStoppedHazard_EndedOutcomeIgnored()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript { Entered = (n, port) => { if (n == 1) port.StartHazard("p", "wisps", "scatter", HazardArgs.Empty); } };
            var fight = Begun(world, script);
            var first = world.Hazards[0].Token;

            ((IPinnacleFightPort)fight).StopHazards();
            fight.OnHazardEnded(first, HazardOutcome.Stopped);
            fight.Tick(1);
            fight.Tick(2);

            Assert.AreEqual(0, script.Events.Count, "a run the core stopped is never delivered");
            Assert.AreEqual(1, world.Hazards.Count, "and never retried");

            // the same through a hold and a phase change
            ((IPinnacleFightPort)fight).StartHazard("q", "balls", "fan", HazardArgs.Empty);
            var second = world.Hazards[1].Token;
            HoldAt(fight, world, 3);
            fight.OnHazardEnded(second, HazardOutcome.Stopped);
            ResumeAt(fight, world, 10);
            fight.Tick(16);

            ((IPinnacleFightPort)fight).StartHazard("r", "balls", "fan", HazardArgs.Empty);
            var third = world.Hazards[2].Token;
            fight.JumpToPhase(2, 17);
            fight.OnHazardEnded(third, HazardOutcome.Completed);
            fight.Tick(18);

            Assert.AreEqual(0, script.Events.Count);
            Assert.AreEqual(3, world.Hazards.Count);
            CollectionAssert.IsSubsetOf(new[] { first, second, third }, world.StoppedTokens);

            foreach (var token in new[] { first, second, third })
                Assert.IsTrue(world.Logged.Any(l => l.Contains($"hazard token {token} ended") && l.Contains("ignored")),
                    $"the late callback for token {token} is logged as ignored");
        }

        [TestMethod]
        public void HazardRefused_RetriedOnce_ThenAborted()
        {
            var world = new FakePinnacleWorld { RefuseNextHazards = 2 };
            var script = new TestScript { Entered = (n, port) => { if (n == 1) port.StartHazard("p2", "wisps", "scatter", HazardArgs.Parse("cycles=6")); } };
            var fight = Begun(world, script);

            Assert.AreEqual(1, world.Hazards.Count);
            Assert.AreEqual(0, script.Events.Count, "no delivery on the first refusal");

            fight.Tick(1);
            Assert.AreEqual(2, world.Hazards.Count, "retried once on the next tick");
            Assert.AreEqual("cycles=6", world.Hazards[1].Args.ToString(), "the same request");
            Assert.AreEqual(1, script.Events.Count);
            Assert.AreEqual(PinnacleEventKind.HazardEnded, script.Events[0].Kind);
            Assert.AreEqual("p2", script.Events[0].Tag);
            Assert.AreEqual(HazardOutcome.Aborted, script.Events[0].Outcome);

            fight.Tick(2);
            fight.Tick(3);
            Assert.AreEqual(2, world.Hazards.Count, "never a third start");
            Assert.AreEqual(1, script.Events.Count);
        }

        [TestMethod]
        public void HazardRefusedOnce_RetrySucceeds_NoEvent()
        {
            var world = new FakePinnacleWorld { RefuseNextHazards = 1 };
            var script = new TestScript { Entered = (n, port) => { if (n == 1) port.StartHazard("p2", "wisps", "scatter", HazardArgs.Empty); } };
            var fight = Begun(world, script);

            fight.Tick(1);
            Assert.AreEqual(2, world.Hazards.Count);
            Assert.IsTrue(world.Hazards[1].Token > 0);
            Assert.AreEqual(0, script.Events.Count);

            fight.OnHazardEnded(world.Hazards[1].Token, HazardOutcome.Completed);
            fight.Tick(2);
            Assert.AreEqual(HazardOutcome.Completed, script.Events.Single().Outcome);
        }

        [TestMethod]
        [DataRow(HazardOutcome.Aborted)]
        [DataRow(HazardOutcome.Reaped)]
        [DataRow(HazardOutcome.Stopped)]
        public void HazardAborted_RetriedOnce(HazardOutcome fault)
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript { Entered = (n, port) => { if (n == 1) port.StartHazard("p4", "balls", "fan", HazardArgs.Parse("waves=6")); } };
            var fight = Begun(world, script);

            fight.OnHazardEnded(world.Hazards[0].Token, fault);
            Assert.AreEqual(1, world.Hazards.Count, "the retry waits for the next tick");
            fight.Tick(1);
            Assert.AreEqual(2, world.Hazards.Count);
            Assert.AreEqual(0, script.Events.Count, "the first fault is not delivered");

            fight.OnHazardEnded(world.Hazards[1].Token, fault);
            fight.Tick(2);
            fight.Tick(3);

            Assert.AreEqual(2, world.Hazards.Count, "retried once only");
            Assert.AreEqual(1, script.Events.Count);
            Assert.AreEqual(HazardOutcome.Aborted, script.Events[0].Outcome, "every fault is delivered as Aborted");
        }

        [TestMethod]
        [DataRow(HazardOutcome.Failed)]
        [DataRow(HazardOutcome.Completed)]
        public void MemoryFailed_NotRetried(HazardOutcome result)
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript { Entered = (n, port) => { if (n == 1) port.StartHazard("round", "memory", null, HazardArgs.Parse("len=4 max_len=4")); } };
            var fight = Begun(world, script);

            fight.OnHazardEnded(world.Hazards[0].Token, result);
            fight.Tick(1);
            fight.Tick(2);

            Assert.AreEqual(1, world.Hazards.Count, "a real result is never retried");
            Assert.AreEqual(1, script.Events.Count);
            Assert.AreEqual(result, script.Events[0].Outcome);
        }

        [TestMethod]
        public void UnknownHazardToken_Ignored()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript();
            var fight = Begun(world, script);

            fight.OnHazardEnded(999, HazardOutcome.Completed);
            fight.Tick(1);

            Assert.AreEqual(0, script.Events.Count);
        }

        [TestMethod]
        public void HazardOrigin_FillsCxCyFromTheHandle()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript { Entered = (n, port) => { if (n == 1) port.StartHazard("fan", "balls", "fan", HazardArgs.Parse("waves=6"), port.Head); } };
            var fight = Begun(world, script);

            var args = world.Hazards.Single().Args;
            Assert.AreEqual(10.6544, args.GetDouble("cx", 0, -1000, 1000), 1e-4);
            Assert.AreEqual(-20.161, args.GetDouble("cy", 0, -1000, 1000), 1e-4);
            Assert.AreEqual(6, args.GetInt("waves", 0, 0, 100));
        }

        // ================================================================ adds

        [TestMethod]
        public void SpawnFailed_RetriedOnce_ThenSyntheticAddDied()
        {
            var world = new FakePinnacleWorld();
            world.FailSpawnsOfWcid[AddWcid] = 2;
            var returned = new PinnacleHandle(-1);
            var script = new TestScript { Entered = (n, port) => { if (n == 1) returned = port.SpawnAdd("grael", AddWcid, P, PinnacleTier.Elite); } };
            var fight = Begun(world, script);

            Assert.AreEqual(2, world.Spawns.Count(s => s.Wcid == AddWcid), "one retry");
            Assert.IsTrue(returned.IsNone);
            Assert.AreEqual(1, script.Events.Count, "delivered once the phase entry returns");
            var e = script.Events[0];
            Assert.AreEqual(PinnacleEventKind.AddDied, e.Kind);
            Assert.AreEqual("grael", e.Tag);
            Assert.IsTrue(e.SpawnFailed);
            Assert.IsTrue(e.Handle.IsNone);

            // 2026-10-19 playtest: the give-up was an INFO line, and every add of every phase failed unseen
            Assert.AreEqual(1, world.Errors.Count, "the give-up is logged at ERROR, exactly once");
            StringAssert.Contains(world.Errors[0], "'grael'");
            StringAssert.Contains(world.Errors[0], AddWcid.ToString());
            StringAssert.Contains(world.Errors[0], "reporting it dead");

            fight.Tick(1);
            fight.Tick(2);
            Assert.AreEqual(1, script.Events.Count);
            Assert.AreEqual(2, world.Spawns.Count(s => s.Wcid == AddWcid), "never a third try");
            Assert.AreEqual(1, world.Errors.Count, "no further ERROR on later ticks");
        }

        [TestMethod]
        public void SpawnFailedOnce_RetrySucceeds_NoEvent()
        {
            var world = new FakePinnacleWorld();
            world.FailSpawnsOfWcid[AddWcid] = 1;
            PinnacleHandle returned = default;
            var script = new TestScript { Entered = (n, port) => { if (n == 1) returned = port.SpawnAdd("grael", AddWcid, P, PinnacleTier.Elite); } };
            var fight = Begun(world, script);
            fight.Tick(1);

            Assert.IsFalse(returned.IsNone);
            Assert.IsTrue(world.IsAlive(returned));
            Assert.AreEqual(0, script.Events.Count);
            Assert.AreEqual(0, world.Errors.Count, "a retry that succeeds is not an ERROR");
            Assert.IsTrue(world.Spawns.Last(s => s.Wcid == AddWcid).WithImmuneRecord, "adds carry the immune record");
        }

        [TestMethod]
        public void AddDeath_DeliveredExactlyOnce()
        {
            var world = new FakePinnacleWorld();
            PinnacleHandle add = default;
            var script = new TestScript { Entered = (n, port) => { if (n == 1) add = port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite); } };
            var fight = Begun(world, script);

            world.Kill(add);
            fight.Tick(1);
            fight.Tick(2);
            fight.Tick(3);

            Assert.AreEqual(1, script.Events.Count);
            Assert.AreEqual("red", script.Events[0].Tag);
            Assert.AreEqual(add, script.Events[0].Handle);
            Assert.IsFalse(script.Events[0].SpawnFailed);
        }

        [TestMethod]
        public void EventFromALeftPhase_IsDropped()
        {
            var world = new FakePinnacleWorld();
            var adds = new List<PinnacleHandle>();
            var script = new TestScript
            {
                Entered = (n, port) => { if (n == 1) { adds.Add(port.SpawnAdd("a", AddWcid, P, PinnacleTier.Elite)); adds.Add(port.SpawnAdd("b", OtherAddWcid, P, PinnacleTier.Elite)); } },
                Evented = (e, port) => { if (e.Kind == PinnacleEventKind.AddDied) port.AdvancePhase(); },
            };
            var fight = Begun(world, script);

            world.Kill(adds[0]);
            world.Kill(adds[1]);
            fight.Tick(1);

            Assert.AreEqual(1, script.Events.Count, "the second death belonged to phase 1");
            Assert.AreEqual(2, fight.Phase, "advanced once, not twice");
        }

        [TestMethod]
        public void CastAtOwner_FacesBobsThenCasts()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript { Entered = (n, port) => { if (n == 1) port.CastAtOwner(port.Head, 4439); } };
            var fight = Begun(world, script);

            var tail = world.Calls.Where(c => c == "FaceOwner" || c == "Bob" || c == "Cast").ToList();
            CollectionAssert.AreEqual(new[] { "FaceOwner", "Bob", "Cast" }, tail);
            Assert.AreEqual((fight.Head, 4439u), world.Casts.Single());
        }

        [TestMethod]
        public void CastAtOwner_CastsOnlyWhenTheNodEnds()
        {
            // C9: the cast is the nod's continuation, not a call made beside it
            var world = new FakePinnacleWorld { DeferNods = true };
            var script = new TestScript { Entered = (n, port) => { if (n == 1) port.CastAtOwner(port.Head, 4439); } };
            var fight = Begun(world, script);

            CollectionAssert.AreEqual(new[] { "FaceOwner", "Bob" }, world.Calls.Where(c => c == "FaceOwner" || c == "Bob" || c == "Cast").ToList());
            Assert.AreEqual(0, world.Casts.Count, "nothing cast while the head is still nodding");

            world.FinishNods();

            Assert.AreEqual((fight.Head, 4439u), world.Casts.Single());
            Assert.AreEqual("Cast", world.Calls.Last());
        }

        [TestMethod]
        public void CastAtOwner_HeldDuringTheNod_NeverCasts()
        {
            var world = new FakePinnacleWorld { DeferNods = true };
            var script = new TestScript { Entered = (n, port) => { if (n == 1) port.CastAtOwner(port.Head, 4439); } };
            var fight = Begun(world, script);

            HoldAt(fight, world, 0.1);
            world.FinishNods();

            Assert.AreEqual(0, world.Casts.Count, "a fight held mid-nod drops the cast");
        }

        [TestMethod]
        public void Held_AddsAndHeadStopFighting_UntilTheResume()
        {
            // C8: peaceful for the hold AND through the resume grace, restored on Resume
            var world = new FakePinnacleWorld();
            PinnacleHandle add = default;
            var script = new TestScript { Entered = (n, port) => { if (n == 1) add = port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite); } };
            var fight = Begun(world, script);
            var head = fight.Head;
            Assert.AreEqual(0, world.Peaceful.Count, "nothing pacified while running");

            HoldAt(fight, world, 10);
            Assert.IsTrue(world.Peaceful[head], "head stops fighting");
            Assert.IsTrue(world.Peaceful[add], "add stops fighting");

            fight.ArmResume();
            world.OwnerPresent = true;
            fight.Tick(20);
            fight.Tick(22);
            Assert.AreEqual(PinnacleFightState.Held, fight.State, "still in the grace");
            Assert.IsTrue(world.Peaceful[head] && world.Peaceful[add], "the returning player is not attacked during the grace");

            fight.Tick(25);
            Assert.AreEqual(PinnacleFightState.Running, fight.State);
            Assert.IsFalse(world.Peaceful[head], "head restored");
            Assert.IsFalse(world.Peaceful[add], "add restored");
        }

        [TestMethod]
        public void Held_AnAddThatDiedInTheHold_IsNotRestored()
        {
            var world = new FakePinnacleWorld();
            PinnacleHandle add = default;
            var script = new TestScript { Entered = (n, port) => { if (n == 1) add = port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite); } };
            var fight = Begun(world, script);

            HoldAt(fight, world, 10);
            world.Kill(add);
            ResumeAt(fight, world, 20);

            Assert.IsTrue(world.Peaceful[add], "a dead add is left alone");
            Assert.IsFalse(world.Peaceful[fight.Head]);
        }

        [TestMethod]
        public void JumpToPhase_FromHeld_RestoresThePacified()
        {
            var world = new FakePinnacleWorld();
            var fight = Begun(world, new TestScript());
            var head = fight.Head;

            HoldAt(fight, world, 10);
            Assert.IsTrue(world.Peaceful[head]);

            fight.JumpToPhase(3, 11);
            Assert.IsFalse(world.Peaceful[head]);
        }

        [TestMethod]
        public void Running_KnotUseBeforeTheAbsentHold_KeepsTheArm()
        {
            // the follow-up arms at teleport start: a Running fight then holds for "owner absent" and must still resume
            var world = new FakePinnacleWorld();
            var fight = Begun(world, new TestScript());

            fight.ArmResume();
            world.OwnerPresent = false;
            fight.Tick(10);
            Assert.AreEqual(PinnacleFightState.Held, fight.State);

            world.OwnerPresent = true;
            fight.Tick(20);
            fight.Tick(25);
            Assert.AreEqual(PinnacleFightState.Running, fight.State, "the kept arm resumes it");
        }

        [TestMethod]
        public void Running_OwnerPresent_ClearsAStaleArm()
        {
            var world = new FakePinnacleWorld();
            var fight = Begun(world, new TestScript());

            fight.ArmResume();
            fight.Tick(5);                  // present: the arm is spent
            world.OwnerPresent = false;
            fight.Tick(10);                 // the absent hold keeps an arm, but there is none left to keep
            Assert.AreEqual(PinnacleFightState.Held, fight.State);
            world.OwnerPresent = true;
            fight.Tick(20);
            fight.Tick(40);

            Assert.AreEqual(PinnacleFightState.Held, fight.State, "no resume without a fresh Knot return");
        }

        [TestMethod]
        public void Forfeit_HeldAttempt_EndsLostAndReportsTheLoss()
        {
            // R1: a held attempt at zero entries is lost through the zero-entry path
            var world = new FakePinnacleWorld();
            var fight = Begun(world, new TestScript());
            HoldAt(fight, world, 10);

            fight.Forfeit("no Knot entries left to return");

            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(PinnacleFightState.Lost, fight.State);
            Assert.AreEqual(1, world.AttemptLostReports);
            Assert.AreEqual(0, world.Refunds);
            Assert.AreEqual(1, world.Releases.Count);
        }

        // ---- telemetry: IPinnacleWorld.ReportEnded ----

        [TestMethod]
        public void EveryEndPath_ReportsEndedExactlyOnce_WithTheRightFlags()
        {
            var paths = new (string Name, Action<PinnacleFight, FakePinnacleWorld> Drive, PinnacleFightState Outcome, string Reason, bool Refund, bool Delivered, string Result)[]
            {
                ("hold expired", (f, w) => { HoldAt(f, w, 10); f.Tick(700); }, PinnacleFightState.Lost, "hold expired", false, false, "expired"),
                ("reset", (f, w) => f.Reset("admin"), PinnacleFightState.Lost, "reset: admin", false, false, "reset"),
                ("forfeit", (f, w) => { HoldAt(f, w, 10); f.Forfeit("no entries"); }, PinnacleFightState.Lost, "forfeit: no entries", false, false, "forfeit"),
                ("copy unloaded", (f, w) => { w.IsLive = false; f.Tick(1); }, PinnacleFightState.Lost, "copy unloaded", false, false, "unloaded"),
                ("won, walked out", (f, w) => { w.Kill(f.Head); f.Tick(1); w.OwnerPresent = false; f.Tick(2); }, PinnacleFightState.Won, "the owner left the copy with the cache delivered", false, true, "won"),
                ("won, collecting expired", (f, w) => { w.Kill(f.Head); f.Tick(1); f.Tick(700); }, PinnacleFightState.Won, "collecting closed at hold expiry", false, true, "won"),
                ("reward never collected", (f, w) => { w.DeliverDefault = false; w.Kill(f.Head); f.Tick(1); f.Tick(700); }, PinnacleFightState.Lost, "reward never collected", true, false, "unpaid"),
            };

            foreach (var (name, drive, outcome, reason, refund, delivered, result) in paths)
            {
                var world = new FakePinnacleWorld();
                var fight = Begun(world, HeadDeathWins());

                drive(fight, world);

                Assert.AreEqual(1, world.Ends.Count, $"{name}: exactly one ReportEnded");
                var e = world.Ends[0];
                Assert.AreEqual(outcome, e.Outcome, name);
                Assert.AreEqual(reason, e.Reason, name);
                Assert.AreEqual(refund, e.Refund, name);
                Assert.AreEqual(delivered, e.CacheDelivered, name);
                Assert.IsTrue(e.Charged, name);
                Assert.AreEqual(250, e.KnotLevel, name);
                Assert.AreEqual(0x80000002u, e.KnotGuid, name);
                Assert.AreEqual(0x50000001u, e.OwnerGuid, name);
                Assert.AreEqual(result, KnotTelemetry.AttemptResultFor(e.Outcome, e.Reason, e.Refund, e.CacheDelivered), name);

                fight.Tick(5000);
                fight.Reset("twice");
                Assert.AreEqual(1, world.Ends.Count, $"{name}: a second end reported again");
            }
        }

        [TestMethod]
        public void ReportEndedThrowing_NeverBreaksTheCleanup()
        {
            var world = new FakePinnacleWorld { ThrowOnReportEnded = true };
            var fight = Begun(world, new TestScript());

            fight.Reset("admin");

            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(PinnacleFightState.Lost, fight.State);
            Assert.AreEqual(1, world.Releases.Count, "released despite the throwing report");
            Assert.AreEqual(1, world.Ends.Count);
        }

        [TestMethod]
        public void Forfeit_IgnoredForWonFights()
        {
            var world = new FakePinnacleWorld();
            var fight = Begun(world, HeadDeathWins(() => world.OwnerPresent = false));
            world.Kill(fight.Head);
            fight.Tick(1);
            Assert.IsTrue(fight.AwaitingDelivery);

            fight.Forfeit("test");

            Assert.IsFalse(fight.IsEnded, "a won fight is never forfeited");
            Assert.AreEqual(0, world.AttemptLostReports);
        }

        // ================================================================ win

        private static TestScript HeadDeathWins(Action onWin = null)
            => new TestScript
            {
                Evented = (e, port) =>
                {
                    if (e.Kind == PinnacleEventKind.AddDied && e.Tag == PinnacleEvent.HeadTag)
                    {
                        port.Win();
                        onWin?.Invoke();
                    }
                },
            };

        [TestMethod]
        public void Win_OwnerPresent_DeliversThenReportsKill()
        {
            var world = new FakePinnacleWorld();
            var script = HeadDeathWins();
            script.Entered = (n, port) => { port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite); port.StartHazard("p5", "wisps", "scatter", HazardArgs.Empty); };
            var fight = Begun(world, script);

            world.Kill(fight.Head);
            fight.Tick(1);

            // C1: delivered, and the fight stays alive (collecting) while the owner is still in the copy
            Assert.AreEqual(PinnacleFightState.Won, fight.State);
            Assert.IsFalse(fight.IsEnded);
            Assert.IsFalse(fight.AwaitingDelivery);
            Assert.IsTrue(fight.Collecting);
            Assert.AreEqual(1, world.DeliverAttempts);
            Assert.AreEqual(0, world.BossKilledReports, "deferred until collecting ends");
            Assert.AreEqual(0, world.Releases.Count, "the copy is kept while collecting");
            Assert.IsNotNull(fight.UnloadIntervalOverride, "collecting keeps the copy");

            foreach (var s in world.Spawns.Where(s => s.Wcid == SealWcid || s.Wcid == AddWcid))
                Assert.IsFalse(world.IsAlive(s.Result), "unsealed and adds cleared");
            Assert.AreEqual(0, world.ActiveHazardCount);

            fight.Tick(50);
            Assert.IsFalse(fight.IsEnded, "still collecting while present");

            // walks out alive: Won reported once
            world.OwnerPresent = false;
            fight.Tick(60);

            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(PinnacleFightState.Won, fight.State);
            Assert.IsFalse(fight.Collecting);
            Assert.AreEqual(1, world.DeliverAttempts);
            Assert.AreEqual(1, world.BossKilledReports);
            Assert.AreEqual(0, world.AttemptLostReports);
            Assert.AreEqual(0, world.Refunds);

            var deliver = world.Calls.IndexOf("TryDeliverCache");
            Assert.IsTrue(deliver < world.Calls.IndexOf("ReportBossKilled"), "the kill is reported only after delivery");
            Assert.IsTrue(world.Calls.IndexOf("ReportBossKilled") < world.Calls.IndexOf("ReleaseInstance"));
            CollectionAssert.AreEqual(new[] { false }, world.Releases);

            fight.Tick(70);
            Assert.AreEqual(1, world.BossKilledReports, "reported once");
        }

        // ---- Knot clear leaderboard: IPinnacleWorld.ReportCleared ----

        [TestMethod]
        public void Win_DeliveredCharged_ReportsExactlyOneClear_WithLevelAndWallSeconds()
        {
            var world = new FakePinnacleWorld();
            var fight = Begun(world, HeadDeathWins());

            world.Kill(fight.Head);
            fight.Tick(5);

            Assert.AreEqual(1, world.Clears.Count);
            Assert.AreEqual(250, world.Clears[0].KnotLevel);
            Assert.AreEqual(5.0, world.Clears[0].WallSeconds, 1e-9);
            Assert.AreEqual(5.0, world.Clears[0].Seconds, 1e-9, "the recorded seconds are the wall clock (owner ruling 2026-10-22)");
            Assert.AreEqual(5.0, world.Clears[0].FightSeconds, 1e-9, "no hold in this run, so the fight clock equals the wall clock");

            fight.Tick(6);
            world.OwnerPresent = false;
            fight.Tick(7);
            fight.Tick(8);
            Assert.AreEqual(1, world.Clears.Count, "reported once, never again while collecting or at the end");
        }

        [TestMethod]
        public void Win_AfterAHold_WallSecondsCountTheDeathAndFightSecondsDoNot()
        {
            var world = new FakePinnacleWorld();
            var fight = Begun(world, HeadDeathWins());

            fight.Tick(2);
            HoldAt(fight, world, 10);
            world.OwnerPresent = true;
            fight.ArmResume();
            fight.Tick(40);

            world.Kill(fight.Head);
            fight.Tick(50);

            Assert.AreEqual(1, world.Clears.Count);
            Assert.AreEqual(50.0, world.Clears[0].WallSeconds, 1e-9);
            Assert.IsTrue(world.Clears[0].FightSeconds < world.Clears[0].WallSeconds, "the Running-only clock excludes the hold");
        }

        [TestMethod]
        public void Win_AdminJumped_ReportsNoClear()
        {
            var world = new FakePinnacleWorld();
            var fight = Begun(world, HeadDeathWins());

            fight.JumpToPhase(2, 1);
            world.Kill(fight.Head);
            fight.Tick(5);

            Assert.IsTrue(fight.Collecting, "the cache was still delivered");
            Assert.AreEqual(0, world.Clears.Count);
        }

        [TestMethod]
        public void RefusedJump_WhileWonAwaitingDelivery_DoesNotSuppressTheClear()
        {
            var world = new FakePinnacleWorld();
            var fight = Begun(world, HeadDeathWins(() => world.OwnerPresent = false));

            world.Kill(fight.Head);
            fight.Tick(1);
            Assert.IsTrue(fight.AwaitingDelivery);

            fight.JumpToPhase(2, 2);   // refused in state Won: no jump happened
            world.OwnerPresent = true;
            fight.Tick(3);

            Assert.IsTrue(fight.Collecting);
            Assert.AreEqual(1, world.Clears.Count);
        }

        [TestMethod]
        public void Win_Uncharged_ReportsNoClear()
        {
            var world = new FakePinnacleWorld();
            var fight = NewFight(world, HeadDeathWins());
            fight.Tick(0);
            Assert.IsTrue(fight.TryBegin(0, charge: false));

            world.Kill(fight.Head);
            fight.Tick(5);

            Assert.AreEqual(0, world.Clears.Count);
        }

        [TestMethod]
        public void Win_NeverDelivered_Refunded_ReportsNoClear()
        {
            var world = new FakePinnacleWorld();
            var fight = Begun(world, HeadDeathWins(() => world.OwnerPresent = false));

            world.Kill(fight.Head);
            fight.Tick(1);
            fight.Tick(601);

            Assert.AreEqual(1, world.Refunds);
            Assert.AreEqual(0, world.Clears.Count);
        }

        [TestMethod]
        public void ReportClearedThrowing_NeverBreaksTheDeliveryOrTheEnd()
        {
            var world = new FakePinnacleWorld { ThrowOnReportCleared = true };
            var fight = Begun(world, HeadDeathWins());

            world.Kill(fight.Head);
            fight.Tick(5);

            Assert.IsTrue(fight.Collecting);
            Assert.AreEqual(1, world.DeliverAttempts);
            Assert.AreEqual(1, world.PortalSummons);

            world.OwnerPresent = false;
            fight.Tick(6);
            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(1, world.BossKilledReports);
        }

        // ---- round 6: the click-to-use return portal on completion (owner ruling 2026-10-20) ----

        [TestMethod]
        public void Win_Delivery_SummonsExactlyOneReturnPortal_RightAfterTheCache()
        {
            var world = new FakePinnacleWorld();
            var fight = Begun(world, HeadDeathWins());

            // two failed placements first: no portal while the cache is still owed
            world.DeliverResults.Enqueue(false);
            world.DeliverResults.Enqueue(false);

            world.Kill(fight.Head);
            fight.Tick(1);
            fight.Tick(2);
            Assert.IsTrue(fight.AwaitingDelivery);
            Assert.AreEqual(0, world.PortalSummons, "never summoned before the cache is delivered");

            fight.Tick(3);
            Assert.IsTrue(fight.Collecting);
            Assert.AreEqual(1, world.PortalSummons, "summoned on the delivery");
            Assert.AreEqual(world.Calls.LastIndexOf("TryDeliverCache") + 1, world.Calls.IndexOf("SummonReturnPortal"), "right after the delivery");

            // collecting ticks, a death beside the cache and the free return never summon a second one
            fight.Tick(10);
            fight.OwnerDied(11);
            world.OwnerPresent = false;
            fight.Tick(12);
            fight.ArmResume();
            world.OwnerPresent = true;
            fight.Tick(13);
            Assert.AreEqual(1, world.PortalSummons, "one portal per fight");

            // the owner uses it: they are alive outside the copy, the existing walk-out path ends the fight Won once
            world.OwnerPresent = false;
            fight.Tick(20);
            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(PinnacleFightState.Won, fight.State);
            Assert.AreEqual(1, world.BossKilledReports);
            CollectionAssert.AreEqual(new[] { false }, world.Releases, "released with the owner outside: the copy (and the portal in it) is destroyed");
            Assert.AreEqual(1, world.PortalSummons);
        }

        [TestMethod]
        public void Win_ReturnPortalThrowsOrFails_CacheStaysDelivered_AndTheFightCollectsNormally()
        {
            foreach (var throws in new[] { true, false })
            {
                var world = new FakePinnacleWorld { ThrowOnSummonPortal = throws, SummonPortalResult = false };
                var fight = Begun(world, HeadDeathWins());

                world.Kill(fight.Head);
                fight.Tick(1);

                var what = throws ? "throw" : "false";
                Assert.AreEqual(1, world.PortalSummons, what);
                Assert.AreEqual(1, world.DeliverAttempts, what);
                Assert.IsFalse(fight.IsEnded, what + ": a summon failure is not a fault end");
                Assert.AreEqual(PinnacleFightState.Won, fight.State, what);
                Assert.IsTrue(fight.Collecting, what + ": the cache is delivered");
                Assert.IsFalse(fight.AwaitingDelivery, what);
                Assert.IsTrue(world.Logged.Any(l => l.Contains("return portal")), what + ": logged");

                fight.Tick(2);
                Assert.AreEqual(1, world.DeliverAttempts, what + ": never a second cache");
                Assert.AreEqual(1, world.PortalSummons, what + ": never retried");

                world.OwnerPresent = false;
                fight.Tick(3);
                Assert.AreEqual(PinnacleFightState.Won, fight.State, what);
                Assert.AreEqual(1, world.BossKilledReports, what + ": the kill is still reported once");
                Assert.AreEqual(0, world.AttemptLostReports, what);
            }
        }

        [TestMethod]
        public void Collecting_OwnerDiesAfterDelivery_KnotReturnReentersTheSameCopy_ThenWalkOutReportsWonOnce()
        {
            // C1: the owner dies after the cache is placed; the copy (and the cache) stays, a Knot return is allowed
            var world = new FakePinnacleWorld();
            var fight = Begun(world, HeadDeathWins());

            world.Kill(fight.Head);
            fight.Tick(1);
            Assert.IsTrue(fight.Collecting);
            var cacheDeliveries = world.DeliverAttempts;

            fight.OwnerDied(10);
            world.OwnerPresent = false;
            world.OwnerAliveElsewhereOverride = true;   // respawned at the lifestone
            fight.Tick(20);
            fight.Tick(200);

            Assert.IsFalse(fight.IsEnded, "a death while collecting allows a return");
            Assert.AreEqual(KnotResume.Free, PinnacleFightManager.ResumeKind(fight.State, fight.AwaitingDelivery, fight.Collecting));
            Assert.AreEqual(0, world.BossKilledReports);
            Assert.AreEqual(0, world.Releases.Count);

            // the Knot return: armed, then present in the copy
            fight.ArmResume();
            fight.Tick(210);
            world.OwnerPresent = true;
            world.OwnerAliveElsewhereOverride = null;
            fight.Tick(220);

            Assert.IsFalse(fight.IsEnded);
            Assert.AreEqual(cacheDeliveries, world.DeliverAttempts, "the same cache: never delivered a second time");

            // walks out again, alive: Won reported exactly once
            world.OwnerPresent = false;
            fight.Tick(230);

            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(PinnacleFightState.Won, fight.State);
            Assert.AreEqual(1, world.BossKilledReports);
            Assert.AreEqual(0, world.AttemptLostReports);
            Assert.AreEqual(0, world.Refunds);
            Assert.AreEqual(1, world.Releases.Count);
        }

        [TestMethod]
        public void Collecting_OwnerLoggedOut_KeptUntilHoldExpiry_ThenWon()
        {
            var world = new FakePinnacleWorld();
            var fight = Begun(world, HeadDeathWins());

            world.Kill(fight.Head);
            fight.Tick(1);

            world.OwnerPresent = false;
            world.OwnerOnline = false;
            fight.Tick(100);
            Assert.IsFalse(fight.IsEnded, "a logout counts as a death: they may come back");

            world.OwnerOnline = true;          // logs back in, outside the copy
            fight.Tick(200);
            Assert.IsFalse(fight.IsEnded, "the logout already allowed a return");

            fight.Tick(600.9);
            Assert.IsFalse(fight.IsEnded);

            fight.Tick(601);
            Assert.IsTrue(fight.IsEnded, "collecting closes at hold expiry, measured from the delivery");
            Assert.AreEqual(PinnacleFightState.Won, fight.State);
            Assert.AreEqual(1, world.BossKilledReports);
        }

        [TestMethod]
        public void Win_OwnerAbsent_DefersDelivery()
        {
            var world = new FakePinnacleWorld();
            var script = HeadDeathWins(() => world.OwnerPresent = false);   // the same blow kills the owner
            var fight = Begun(world, script);

            world.Kill(fight.Head);
            fight.Tick(1);

            Assert.AreEqual(PinnacleFightState.Won, fight.State);
            Assert.IsTrue(fight.AwaitingDelivery);
            Assert.IsFalse(fight.IsEnded);
            Assert.AreEqual(0, world.DeliverAttempts, "never delivered to an absent owner");
            Assert.IsNotNull(fight.UnloadIntervalOverride, "the copy is kept for the delivery");

            fight.OwnerDied(2);
            fight.Tick(100);
            Assert.AreEqual(PinnacleFightState.Won, fight.State, "a won fight never holds");
            Assert.AreEqual(0, world.BossKilledReports);

            world.OwnerPresent = true;
            fight.Tick(200);

            Assert.IsFalse(fight.IsEnded, "delivered on return; collecting now");
            Assert.IsTrue(fight.Collecting);
            Assert.AreEqual(1, world.DeliverAttempts);
            Assert.AreEqual(0, world.BossKilledReports);

            world.OwnerPresent = false;
            fight.Tick(210);

            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(PinnacleFightState.Won, fight.State);
            Assert.AreEqual(1, world.BossKilledReports);
            Assert.AreEqual(0, world.AttemptLostReports);
        }

        [TestMethod]
        public void Win_OwnerNeverReturns_RefundsTheEntry_NoLossReported()
        {
            // C2: an unpaid win refunds the charged entry and reports no loss
            var world = new FakePinnacleWorld();
            var script = HeadDeathWins(() => world.OwnerPresent = false);
            var fight = Begun(world, script);
            Assert.AreEqual(1, world.Charges);

            world.Kill(fight.Head);
            fight.Tick(1);
            fight.Tick(600.9);
            Assert.IsFalse(fight.IsEnded);

            fight.Tick(601);
            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(PinnacleFightState.Lost, fight.State);
            Assert.AreEqual(0, world.BossKilledReports);
            Assert.AreEqual(0, world.AttemptLostReports, "no loss: the entry is given back instead");
            Assert.AreEqual(1, world.Refunds);
            Assert.AreEqual(0, world.DeliverAttempts);

            fight.Tick(700);
            Assert.AreEqual(1, world.Refunds, "refunded once");
        }

        [TestMethod]
        public void OfflineOwner_TheRefundAndTheLossStillReachTheWorld_WhichHoldsThemForTheirReturn()
        {
            // review item 4: the core never drops an event for an offline owner; it hands it to the world, whose live
            // half (LivePinnacleWorld.DeferIfOffline) holds it in KnotPendingEvents
            var world = new FakePinnacleWorld();
            var fight = Begun(world, HeadDeathWins(() => { world.OwnerPresent = false; world.OwnerOnline = false; }));

            world.Kill(fight.Head);
            fight.Tick(1);
            fight.Tick(601);

            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(1, world.Refunds, "an unpaid win of a logged-out owner is still refunded");
            Assert.AreEqual(0, world.AttemptLostReports);

            var held = new FakePinnacleWorld();
            var lost = Begun(held, new TestScript());
            HoldAt(lost, held, 10);
            held.OwnerOnline = false;
            lost.Tick(700);

            Assert.AreEqual(PinnacleFightState.Lost, lost.State);
            Assert.AreEqual(1, held.AttemptLostReports, "a held attempt that expires while its owner is offline is still reported lost");
        }

        [TestMethod]
        public void Win_DeliveryKeepsFailing_RefundsAtHold()
        {
            var world = new FakePinnacleWorld { DeliverDefault = false };
            var fight = Begun(world, HeadDeathWins());

            world.Kill(fight.Head);
            fight.Tick(1);
            fight.Tick(300);
            fight.Tick(601);

            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(1, world.Refunds);
            Assert.AreEqual(0, world.AttemptLostReports);
            Assert.AreEqual(0, world.BossKilledReports);
        }

        [TestMethod]
        public void Win_DeliveryFails_RetriesNextTick()
        {
            var world = new FakePinnacleWorld();
            world.DeliverResults.Enqueue(false);
            world.DeliverResults.Enqueue(false);
            var script = HeadDeathWins();
            var fight = Begun(world, script);

            world.Kill(fight.Head);
            fight.Tick(1);
            Assert.AreEqual(1, world.DeliverAttempts);
            Assert.IsTrue(fight.AwaitingDelivery);
            Assert.AreEqual(0, world.BossKilledReports, "the Knot is not told before the cache exists");

            fight.Tick(2);
            Assert.AreEqual(2, world.DeliverAttempts);
            Assert.AreEqual(0, world.BossKilledReports);

            fight.Tick(3);
            Assert.AreEqual(3, world.DeliverAttempts);
            Assert.IsTrue(fight.Collecting, "the third attempt places the cache");
            Assert.AreEqual(0, world.BossKilledReports);

            fight.Tick(4);
            Assert.AreEqual(3, world.DeliverAttempts, "never delivered again once placed");

            world.OwnerPresent = false;
            fight.Tick(5);
            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(1, world.BossKilledReports);
        }

        // ================================================================ end paths

        [TestMethod]
        public void EveryEndPath_SameCleanup()
        {
            var paths = new (string Name, Action<PinnacleFight, FakePinnacleWorld> Drive, PinnacleFightState Expected, bool OwnerInside)[]
            {
                ("hold expired", (f, w) => { HoldAt(f, w, 10); f.Tick(700); }, PinnacleFightState.Lost, false),
                ("reset running", (f, w) => f.Reset("admin"), PinnacleFightState.Lost, true),
                ("reset held", (f, w) => { HoldAt(f, w, 10); f.Reset("admin"); }, PinnacleFightState.Lost, false),
                ("won, walked out", (f, w) => { w.Kill(f.Head); f.Tick(1); w.OwnerPresent = false; f.Tick(2); }, PinnacleFightState.Won, false),
                ("won, collecting expired", (f, w) => { w.Kill(f.Head); f.Tick(1); f.Tick(700); }, PinnacleFightState.Won, true),
                ("reward never collected", (f, w) => { w.DeliverDefault = false; w.Kill(f.Head); f.Tick(1); f.Tick(700); }, PinnacleFightState.Lost, true),
            };

            foreach (var (name, drive, expected, ownerInside) in paths)
            {
                var world = new FakePinnacleWorld();
                var script = HeadDeathWins();
                script.Entered = (n, port) =>
                {
                    port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite);
                    port.StartHazard("h", "wisps", "scatter", HazardArgs.Empty);
                    port.SetTimer("t", 1000);
                };
                var fight = Begun(world, script);

                drive(fight, world);

                Assert.IsTrue(fight.IsEnded, name);
                Assert.AreEqual(expected, fight.State, name);
                CollectionAssert.AreEqual(new[] { ownerInside }, world.Releases, $"{name}: released exactly once");

                foreach (var s in world.Spawns.Where(s => !s.Result.IsNone))
                    Assert.IsFalse(world.IsAlive(s.Result), $"{name}: wcid {s.Wcid} left alive");

                Assert.AreEqual(0, world.ActiveHazardCount, $"{name}: a hazard left running");
                Assert.AreEqual(world.Destroyed.Count, world.Destroyed.Distinct().Count(), $"{name}: something destroyed twice");
                var refunded = name == "reward never collected";
                Assert.AreEqual(expected == PinnacleFightState.Lost && !refunded ? 1 : 0, world.AttemptLostReports, name);
                Assert.AreEqual(refunded ? 1 : 0, world.Refunds, name);
                Assert.AreEqual(expected == PinnacleFightState.Won ? 1 : 0, world.BossKilledReports, name);
                Assert.IsFalse(fight.Collecting || fight.AwaitingDelivery, $"{name}: no Won stage flag left set");                Assert.IsNull(fight.UnloadIntervalOverride, name);

                var calls = world.Calls.Count;
                fight.Tick(5000);
                fight.Reset("twice");
                fight.JumpToPhase(2, 5001);
                fight.OwnerDied(5002);
                Assert.AreEqual(calls, world.Calls.Count, $"{name}: a second end touched the world");
            }
        }

        [TestMethod]
        public void NotLive_EndsLost_NoWorldWrites()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript
            {
                Entered = (n, port) =>
                {
                    port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite);
                    port.StartHazard("h", "wisps", "scatter", HazardArgs.Empty);
                },
            };
            var fight = Begun(world, script);
            HoldAt(fight, world, 10);

            world.IsLive = false;
            var mark = world.Calls.Count;
            fight.Tick(11);

            Assert.AreEqual(PinnacleFightState.Lost, fight.State);
            Assert.IsTrue(fight.IsEnded);
            CollectionAssert.AreEqual(new[] { "ReportAttemptLost" }, world.CallsSince(mark).ToList(),
                "only the Knot is told; nothing touches the unloaded copy");

            fight.Tick(12);
            Assert.AreEqual(mark + 1, world.Calls.Count);
        }

        [TestMethod]
        public void UnchargedAttempt_IsNeverReportedLost()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript();
            var notStarted = NewFight(world, script);
            notStarted.Tick(0);
            notStarted.Reset("admin");
            Assert.AreEqual(0, world.AttemptLostReports, "never begun");

            var world2 = new FakePinnacleWorld();
            var jumped = NewFight(world2, new TestScript());
            jumped.Tick(0);
            jumped.JumpToPhase(3, 0);
            Assert.AreEqual(0, world2.Charges, "an admin jump begins without a charge");
            jumped.Reset("admin");
            Assert.AreEqual(0, world2.AttemptLostReports);
        }

        // ================================================================ admin, timers, unload

        [TestMethod]
        public void JumpToPhase_ClearsPhaseHandles_KeepsHead()
        {
            var world = new FakePinnacleWorld();
            PinnacleHandle add = default;
            var script = new TestScript
            {
                Entered = (n, port) =>
                {
                    if (n != 1)
                        return;
                    add = port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite);
                    port.StartHazard("h", "wisps", "scatter", HazardArgs.Empty);
                    port.SetTimer("t", 5);
                },
            };
            var fight = Begun(world, script);
            var head = fight.Head;
            var token = world.Hazards[0].Token;

            fight.JumpToPhase(3, 1);

            Assert.AreEqual(3, fight.Phase);
            Assert.AreEqual("Enter:3", script.Log.Last());
            Assert.IsFalse(world.IsAlive(add), "phase add destroyed");
            CollectionAssert.Contains(world.Destroyed, add);
            Assert.IsTrue(world.IsAlive(head), "head kept");
            CollectionAssert.DoesNotContain(world.Destroyed, head);
            Assert.AreEqual(head, fight.Head);
            Assert.IsTrue(world.Spawns.Where(s => s.Wcid == SealWcid).All(s => world.IsAlive(s.Result)), "seal kept");
            CollectionAssert.Contains(world.StoppedTokens, token);

            fight.Tick(20);
            Assert.IsFalse(script.Events.Any(e => e.Kind == PinnacleEventKind.TimerFired), "phase timers cancelled");

            fight.JumpToPhase(9, 21);
            Assert.AreEqual(3, fight.Phase, "out of range refused");
        }

        [TestMethod]
        public void Timers_FrozenWhileHeld()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript { Entered = (n, port) => { if (n == 1) port.SetTimer("cast", 10); } };
            var fight = Begun(world, script);

            fight.Tick(5);
            HoldAt(fight, world, 5);
            fight.Tick(100);
            fight.Tick(400);
            Assert.AreEqual(0, script.Events.Count, "no timer fires while held");

            ResumeAt(fight, world, 500);          // resumed at monotonic 505, fight clock 5
            Assert.AreEqual(5, fight.Now, 1e-9);

            fight.Tick(509);
            Assert.AreEqual(0, script.Events.Count);

            fight.Tick(510);
            Assert.AreEqual(1, script.Events.Count);
            Assert.AreEqual(PinnacleEventKind.TimerFired, script.Events[0].Kind);
            Assert.AreEqual("cast", script.Events[0].Tag);
        }

        [TestMethod]
        public void UnloadOverride_OnlyHeldOrAwaiting()
        {
            var world = new FakePinnacleWorld();
            var script = HeadDeathWins();
            var fight = NewFight(world, script);
            fight.Tick(0);
            Assert.IsNull(fight.UnloadIntervalOverride, "NotStarted");

            fight.TryBegin(0);
            fight.Tick(5);
            Assert.IsNull(fight.UnloadIntervalOverride, "Running");

            HoldAt(fight, world, 10);
            fight.Tick(110);
            Assert.AreEqual(TimeSpan.FromSeconds(600 + 60), fight.UnloadIntervalOverride, "Held: the whole hold plus the margin (an idle window, not a deadline)");

            ResumeAt(fight, world, 120);
            Assert.IsNull(fight.UnloadIntervalOverride, "Running again");

            world.DeliverDefault = false;
            world.Kill(fight.Head);
            fight.Tick(130);
            Assert.IsTrue(fight.AwaitingDelivery);
            Assert.AreEqual(TimeSpan.FromSeconds(600 + 60), fight.UnloadIntervalOverride, "Won and awaiting");

            fight.Reset("done");
            Assert.IsNull(fight.UnloadIntervalOverride, "ended");
        }

        /// <summary>
        /// The override is fed to Landblock.ShouldQueueForUnload, which measures it from the landblock's idle clock
        /// (frozen once the owner left, about when the hold began). A REMAINING-hold override shrank while that clock
        /// stood still and queued the copy for unload about 330 s into a 600 s hold; this drives the real decision
        /// through every second of the hold.
        /// </summary>
        [TestMethod]
        public void HeldCopy_NotQueuedForUnload_UntilTheHoldEnds()
        {
            var world = new FakePinnacleWorld();
            var fight = Begun(world, HeadDeathWins());
            var lastActive = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

            HoldAt(fight, world, 10);

            for (var e = 0; e < 600; e++)
            {
                fight.Tick(10 + e);
                Assert.AreEqual(PinnacleFightState.Held, fight.State, $"still held at {e}s");

                var window = fight.UnloadIntervalOverride ?? ACE.Server.Entity.Landblock.UnloadInterval;
                Assert.IsFalse(ACE.Server.Entity.Landblock.ShouldQueueForUnload(0, lastActive, lastActive.AddSeconds(e), window),
                    $"queued for unload {e}s into the hold");
            }

            fight.Tick(610);
            Assert.IsTrue(fight.IsEnded, "the hold expired");
            Assert.IsNull(fight.UnloadIntervalOverride, "an ended fight releases the copy to the default window");
            Assert.IsTrue(ACE.Server.Entity.Landblock.ShouldQueueForUnload(0, lastActive, lastActive.AddSeconds(600), ACE.Server.Entity.Landblock.UnloadInterval));
        }

        [TestMethod]
        public void AdvanceInLastPhase_Ignored()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript { PhaseCount = 2 };
            var fight = Begun(world, script);
            fight.JumpToPhase(2, 1);

            ((IPinnacleFightPort)fight).AdvancePhase();
            fight.Tick(2);

            Assert.AreEqual(2, fight.Phase);
            Assert.AreEqual(PinnacleFightState.Running, fight.State);
        }

        // ================================================================ review follow-up (F1-F6)

        [TestMethod]
        public void ScriptThrowsInEnterPhase_EndsLostOnce_Cleaned()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript
            {
                Entered = (n, port) =>
                {
                    port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite);
                    port.StartHazard("h", "wisps", "scatter", HazardArgs.Empty);
                    throw new InvalidOperationException("script bug");
                },
            };
            var fight = NewFight(world, script);
            fight.Tick(0);

            var begun = fight.TryBegin(1);   // must not throw

            Assert.IsTrue(begun, "the begin had started");
            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(PinnacleFightState.Lost, fight.State);
            Assert.AreEqual(1, world.Charges, "the entry already spent stays spent");
            Assert.AreEqual(0, world.AttemptLostReports, "a fault never consumes the Knot");
            Assert.AreEqual(1, world.Releases.Count);
            foreach (var s in world.Spawns.Where(s => !s.Result.IsNone))
                Assert.IsFalse(world.IsAlive(s.Result), $"wcid {s.Wcid} cleaned up (seal, add, head)");
            Assert.AreEqual(0, world.ActiveHazardCount);
            Assert.IsTrue(world.Logged.Any(l => l.Contains("begin threw")));
            Assert.IsTrue(world.Logged.Any(l => l.Contains("ended Lost (fault in begin)")));

            var calls = world.Calls.Count;
            fight.Tick(2);
            fight.Reset("again");
            Assert.AreEqual(calls, world.Calls.Count, "ended once");
        }

        [TestMethod]
        public void ScriptThrowsInOnEvent_EndsLost()
        {
            var world = new FakePinnacleWorld();
            PinnacleHandle add = default;
            var script = new TestScript
            {
                Entered = (n, port) => { add = port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite); port.SpawnAdd("blue", OtherAddWcid, P, PinnacleTier.Elite); },
                Evented = (e, port) => { port.AdvancePhase(); throw new InvalidOperationException("handler bug"); },
            };
            var fight = Begun(world, script);

            world.Kill(add);
            fight.Tick(1);   // must not throw

            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(PinnacleFightState.Lost, fight.State);
            Assert.AreEqual(1, fight.Phase, "the latched advance died with the fault");
            Assert.AreEqual(0, world.AttemptLostReports);
            Assert.AreEqual(1, world.Releases.Count);
            Assert.IsFalse(world.IsAlive(world.HandleOf(OtherAddWcid)), "surviving add cleaned up");
        }

        [TestMethod]
        public void EndStepThrows_StillReleasesAndReports()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript
            {
                Entered = (n, port) =>
                {
                    port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite);
                    port.StartHazard("h", "wisps", "scatter", HazardArgs.Empty);
                },
            };
            var fight = Begun(world, script);
            var token = world.Hazards[0].Token;
            HoldAt(fight, world, 10);

            world.ThrowOnDestroy = true;
            fight.Tick(610);   // hold expired: a normal (not fault) end whose destroys all throw

            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(PinnacleFightState.Lost, fight.State);
            Assert.AreEqual(1, world.AttemptLostReports, "a non-fault end still reports the lost attempt");
            Assert.AreEqual(1, world.Releases.Count, "still released");
            CollectionAssert.Contains(world.StoppedTokens, token);
            Assert.AreEqual(4, world.Destroyed.Count, "every destroy attempted (add, two seals, head) despite each throwing");
            Assert.IsTrue(world.Logged.Count(l => l.Contains("cleanup step")) >= 4);
        }

        [TestMethod]
        public void WinReportThrows_NoSecondCache()
        {
            var world = new FakePinnacleWorld { ThrowOnReportBossKilled = true };
            var script = HeadDeathWins();
            var fight = Begun(world, script);

            world.Kill(fight.Head);
            fight.Tick(1);
            world.OwnerPresent = false;   // collecting ends: the report throws inside End
            fight.Tick(2);
            fight.Tick(3);

            Assert.AreEqual(1, world.DeliverAttempts, "never a second cache");
            Assert.AreEqual(1, world.BossKilledReports);
            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(PinnacleFightState.Won, fight.State, "the cache was delivered: still a win");
            Assert.AreEqual(0, world.AttemptLostReports);
            Assert.AreEqual(1, world.Releases.Count);
        }

        [TestMethod]
        public void TryBegin_HeadSpawnFails_RefusedNoCharge()
        {
            var world = new FakePinnacleWorld();
            world.FailSpawnsOfWcid[HeadWcid] = 1000;
            var script = new TestScript();
            var fight = NewFight(world, script);
            fight.Tick(0);

            Assert.IsFalse(fight.TryBegin(1));

            Assert.AreEqual(PinnacleFightState.NotStarted, fight.State);
            Assert.IsFalse(fight.IsEnded);
            Assert.AreEqual(0, world.Charges);
            Assert.AreEqual(0, world.Spawns.Count(s => s.Wcid == SealWcid), "not sealed");
            Assert.AreEqual(0, script.Log.Count);

            world.FailSpawnsOfWcid[HeadWcid] = 0;
            fight.Tick(2);
            Assert.IsTrue(fight.TryBegin(3), "begins once the head exists");
            Assert.AreEqual(1, world.Charges);
        }

        [TestMethod]
        public void TryBegin_HeadKilledBeforeStart_Respawned()
        {
            var world = new FakePinnacleWorld();
            var script = new TestScript();
            var fight = NewFight(world, script);
            fight.Tick(0);
            var first = fight.Head;

            world.Kill(first);
            fight.Tick(1);
            var second = fight.Head;
            Assert.AreNotEqual(first, second, "a dead head is respawned on the tick");
            Assert.IsTrue(world.IsAlive(second));

            world.Kill(second);
            Assert.IsTrue(fight.TryBegin(2), "and by the begin itself");
            Assert.AreEqual(3, world.Spawns.Count(s => s.Wcid == HeadWcid));
            Assert.IsTrue(world.IsAlive(fight.Head));
        }

        [TestMethod]
        public void SpawnFailed_ScriptRespawnsOnDeath_DoesNotSpin()
        {
            var world = new FakePinnacleWorld();
            world.FailSpawnsOfWcid[AddWcid] = 1000;
            var script = new TestScript
            {
                Entered = (n, port) => port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite),
                Evented = (e, port) => { if (e.SpawnFailed) port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite); },
            };
            var fight = Begun(world, script);

            Assert.AreEqual(1, script.Events.Count, "one delivery per drain");
            fight.Tick(1);
            Assert.AreEqual(2, script.Events.Count);
            fight.Tick(2);
            Assert.AreEqual(3, script.Events.Count);
            Assert.AreEqual(8, world.Spawns.Count(s => s.Wcid == AddWcid), "two tries per spawn, four spawns");
        }

        [TestMethod]
        public void OnStartEvents_SurviveIntoPhase1()
        {
            var world = new FakePinnacleWorld();
            PinnacleHandle add = default;
            var script = new TestScript
            {
                Started = port => { add = port.SpawnAdd("intro", AddWcid, P, PinnacleTier.Elite); port.SetTimer("intro", 2); },
            };
            var fight = Begun(world, script);

            Assert.IsTrue(world.IsAlive(add), "the first phase entry does not clear OnStart's add");

            world.Kill(add);
            fight.Tick(2);

            CollectionAssert.AreEquivalent(new[] { "AddDied(intro, " + add + ")", "TimerFired(intro)" },
                script.Events.Select(e => e.ToString()).ToList());
        }

        [TestMethod]
        public void Resume_EventDrainWins_SkipsRestartPhase()
        {
            var world = new FakePinnacleWorld();
            var script = HeadDeathWins();
            var fight = Begun(world, script);
            HoldAt(fight, world, 10);

            world.Kill(fight.Head);   // a DoT kills the head during the hold
            fight.Tick(11);
            Assert.AreEqual(PinnacleFightState.Held, fight.State);

            fight.ArmResume();
            world.OwnerPresent = true;
            fight.Tick(20);
            fight.Tick(25);

            Assert.IsFalse(script.Log.Any(l => l.StartsWith("Restart")), "a win at resume skips RestartPhase");
            Assert.AreEqual(PinnacleFightState.Won, fight.State);
            Assert.IsTrue(fight.Collecting, "delivered on the resume tick");

            world.OwnerPresent = false;
            fight.Tick(30);
            Assert.IsTrue(fight.IsEnded);
            Assert.AreEqual(1, world.BossKilledReports);
        }

        [TestMethod]
        public void JumpToPhase_FromHeld()
        {
            var world = new FakePinnacleWorld();
            PinnacleHandle add = default;
            var script = new TestScript { Entered = (n, port) => { if (n == 1) add = port.SpawnAdd("red", AddWcid, P, PinnacleTier.Elite); } };
            var fight = Begun(world, script);
            fight.Tick(4);
            HoldAt(fight, world, 4);
            world.OwnerPresent = true;   // the admin is standing in the copy

            fight.JumpToPhase(3, 50);

            Assert.AreEqual(PinnacleFightState.Running, fight.State);
            Assert.AreEqual(3, fight.Phase);
            Assert.IsFalse(script.Log.Any(l => l.StartsWith("Restart")));
            Assert.IsFalse(world.IsAlive(add));
            Assert.IsNull(fight.UnloadIntervalOverride);

            fight.Tick(60);
            Assert.AreEqual(14, fight.Now, 1e-9, "clock resumes from the jump, not across the hold");

            var clamps = world.Clamps.Count;
            fight.Tick(61);
            Assert.AreEqual(clamps, world.Clamps.Count, "no hold clamping once running");
        }

        // ================================================================ plate-floor lift (2026-10-22)

        /// <summary>
        /// The lift check runs on every live tick in EVERY state (NotStarted, Running, Held, Won), with the tick's now,
        /// and never once the attempt has ended or its copy is gone.
        /// </summary>
        [TestMethod]
        public void PlateLift_IsCheckedOnEveryLiveTick_InEveryState_AndNeverAfterTheEnd()
        {
            var world = new FakePinnacleWorld();
            var fight = NewFight(world, HeadDeathWins());

            fight.Tick(0);                                   // NotStarted
            Assert.IsTrue(fight.TryBegin(0));
            fight.Tick(1);                                   // Running
            HoldAt(fight, world, 2);
            fight.Tick(3);                                   // Held, owner away
            ResumeAt(fight, world, 10);                      // Held (10), then Running (15)
            world.Kill(fight.Head);
            fight.Tick(16);                                  // Won: delivered, collecting
            Assert.AreEqual(PinnacleFightState.Won, fight.State);
            Assert.IsFalse(fight.IsEnded);

            CollectionAssert.AreEqual(new[] { 0.0, 1.0, 3.0, 10.0, 15.0, 16.0 }, world.PlateLiftChecks, string.Join(",", world.PlateLiftChecks));

            world.OwnerPresent = false;
            fight.Tick(17);                                  // walked out: ends during this tick (the check ran first)
            Assert.IsTrue(fight.IsEnded);

            fight.Tick(18);
            Assert.AreEqual(17.0, world.PlateLiftChecks.Last(), "no check after the end");

            // a copy that is gone: no check
            var world2 = new FakePinnacleWorld { IsLive = false };
            NewFight(world2, new TestScript()).Tick(0);
            Assert.AreEqual(0, world2.PlateLiftChecks.Count, "a dead copy is never checked");
        }

        /// <summary>A throwing lift is logged and the fight goes on: it is not a fault.</summary>
        [TestMethod]
        public void PlateLift_Throwing_IsLogged_AndDoesNotEndTheFight()
        {
            var world = new FakePinnacleWorld { ThrowOnPlateLift = true };
            var fight = Begun(world, new TestScript());

            fight.Tick(1);
            fight.Tick(2);

            Assert.IsFalse(fight.IsEnded, "a lift failure never faults the attempt");
            Assert.AreEqual(PinnacleFightState.Running, fight.State);
            Assert.AreEqual(3, world.PlateLiftChecks.Count, "ticks 0, 1 and 2 all reached the world");
            Assert.IsTrue(world.Logged.Any(l => l.Contains("plate-floor lift threw")), "logged");

            Assert.IsFalse(world.Logged.Any(l => l.Contains("fault in")), "no fault end was taken");
        }

        [TestMethod]
        public void SameSeed_SameRng()
        {
            var a = NewFight(new FakePinnacleWorld(), new TestScript());
            var b = NewFight(new FakePinnacleWorld(), new TestScript());

            Assert.AreEqual(a.Rng.Next(), b.Rng.Next());
        }
    }
}
