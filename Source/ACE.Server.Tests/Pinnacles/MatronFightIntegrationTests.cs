using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Matron;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The REAL PinnacleFight driving the REAL MatronScript on FakePinnacleWorld (review F7). Pins what the script tests
    /// cannot: the core's resume runs RestorePacified (SetPeaceful false) BEFORE RestartPhase, so the script's restart
    /// must win the last word on her temperament and her walk or held motion; and the core's one-shot hazard retry
    /// relaunches the fill with its ORIGINAL args, which only the script's poll can correct. Reads no PropertyManager value.
    /// </summary>
    [TestClass]
    public class MatronFightIntegrationTests
    {
        private static PinnacleFightConfig Cfg => new PinnacleFightConfig { HoldSeconds = 600, ResumeGraceSeconds = 5 };

        private static (PinnacleFight Fight, FakePinnacleWorld World, MatronScript Script, PinnacleHandle Head) Begun()
        {
            var world = new FakePinnacleWorld();
            var script = new MatronScript();
            var fight = new PinnacleFight(script, world, Cfg, ownerGuid: 0x50000001, knotGuid: 0x80000002, instance: 7, knotLevel: 250, seed: 42);
            fight.Tick(0);
            Assert.IsTrue(fight.TryBegin(0));
            Assert.AreEqual(PinnacleFightState.Running, fight.State);
            return (fight, world, script, world.HandleOf(MatronData.HeadWcid));
        }

        /// <summary>The owner dies at <paramref name="at"/>, re-enters, and is present through the grace: the core resumes.</summary>
        private static double DieAndReturn(PinnacleFight fight, FakePinnacleWorld world, double at)
        {
            fight.OwnerDied(at);
            world.OwnerPresent = false;
            Assert.AreEqual(PinnacleFightState.Held, fight.State);

            fight.ArmResume();
            world.OwnerPresent = true;
            fight.Tick(at + 0.1);
            fight.Tick(at + 5.2);
            Assert.AreEqual(PinnacleFightState.Running, fight.State, "resumed after the grace");
            return at + 5.2;
        }

        [TestMethod]
        public void Death_WhileRunningToAPit_ResumesPeacefulAndWalkingToThatPit()
        {
            var (fight, world, script, head) = Begun();

            world.AtFloor.Add(head);
            fight.Tick(1.1);   // the first poll: the 80% threshold
            world.AtFloor.Remove(head);
            Assert.AreEqual(MatronStep.Running, script.Step);

            var stand = MatronData.Opening(script.PitOrder[0]).Stand;
            Assert.AreEqual(stand, world.MoveTos.Last().To);

            var engagedBefore = world.Engaged.Count;

            DieAndReturn(fight, world, 1.5);

            Assert.IsTrue(world.Peaceful[head], "the restart re-asserted peaceful after the core's RestorePacified");
            Assert.AreEqual(stand, world.MoveTos.Last().To, "and walks her on to the same pit");
            Assert.AreEqual(engagedBefore, world.Engaged.Count, "never Engaged after the restart (it would cancel the walk)");

            Assert.AreEqual(MatronStep.Running, script.Step);
        }

        /// <summary>An admin jump straight into phase 2, her walk already at the finale point: she turns and plays the opening.</summary>
        private static (PinnacleFight Fight, FakePinnacleWorld World, MatronScript Script, PinnacleHandle Head) InFinaleOpening()
        {
            var world = new FakePinnacleWorld();
            var script = new MatronScript();
            var fight = new PinnacleFight(script, world, Cfg, ownerGuid: 0x50000001, knotGuid: 0x80000002, instance: 7, knotLevel: 250, seed: 42);
            fight.Tick(0);
            fight.JumpToPhase(2, 0);   // the admin path: OnStart then EnterPhase(2), no phase 1
            var head = world.HandleOf(MatronData.HeadWcid);
            Assert.AreEqual(MatronStep.FinaleWalking, script.Step);

            world.PositionOf[head] = MatronData.FinalePoint;
            fight.Tick(0.3);   // the walk poll (every 0.25 s): arrived, the turn begins
            Assert.AreEqual(MatronStep.FinaleOpening, script.Step);
            Assert.AreEqual(MatronData.FinalePoint, world.Turns.Last().Facing);
            return (fight, world, script, head);
        }

        [TestMethod]
        public void Death_WhileFinaleOpening_ResumesPeaceful_TurnsAndPlaysTheOpeningAgain_NeverFallen()
        {
            var (fight, world, script, head) = InFinaleOpening();
            fight.Tick(0.5);   // the face timer: the opening motion
            Assert.AreEqual(new FakePinnacleWorld.MotionCall(head, MotionStance.HandCombat, MotionCommand.SpecialAttack1, false), world.Motions.Last());

            var engagedBefore = world.Engaged.Count;
            var turnsBefore = world.Turns.Count;
            var motionsBefore = world.Motions.Count;

            var t = DieAndReturn(fight, world, 1.0);

            Assert.IsTrue(world.Peaceful[head], "peaceful after the core's RestorePacified");
            Assert.AreEqual(turnsBefore + 1, world.Turns.Count, "she turns to the centre again");
            Assert.AreEqual(motionsBefore, world.Motions.Count, "the motion waits for the turn");
            fight.Tick(t + 0.2);
            Assert.AreEqual(new FakePinnacleWorld.MotionCall(head, MotionStance.HandCombat, MotionCommand.SpecialAttack1, false), world.Motions.Last(), "and plays the opening again");
            Assert.IsFalse(world.Motions.Any(m => m.Motion == MotionCommand.Fallen), "she never lies down");
            Assert.AreEqual(engagedBefore, world.Engaged.Count, "never Engaged");
            Assert.AreEqual(MatronStep.FinaleOpening, script.Step);

            fight.Tick(t + 0.2 + MatronScript.RumbleSeconds + 0.1);
            Assert.AreEqual(MatronStep.FinaleRace, script.Step, "the rumble follows the replayed motion");
        }

        [TestMethod]
        public void Finale_RockKeptThroughADeath_ItsDeathLetsTheLavaOut_SlayAt5s_PillarsAt19s_WinWithPillarsStanding()
        {
            var (fight, world, script, head) = InFinaleOpening();
            fight.Tick(0.5);                                  // the opening motion
            fight.Tick(0.5 + MatronScript.RumbleSeconds + 0.1);
            Assert.AreEqual(MatronStep.FinaleRace, script.Step);

            var rock = world.HandleOf(MatronData.RockWcid);
            var rockSpawns = world.Spawns.Count(s => s.Wcid == MatronData.RockWcid);
            Assert.AreEqual(1, rockSpawns);

            // a death in the race keeps the living rock (adds stay through the hold)
            var t = DieAndReturn(fight, world, 3.0);
            Assert.AreEqual(MatronStep.FinaleRace, script.Step);
            Assert.IsTrue(world.IsAlive(rock), "the rock survived the hold");
            Assert.AreEqual(rockSpawns, world.Spawns.Count(s => s.Wcid == MatronData.RockWcid), "no second rock");

            // the rock breaks: the lava comes out on the next tick
            world.Kill(rock);
            t += 0.2;
            fight.Tick(t);
            Assert.AreEqual(MatronStep.FinaleLava, script.Step);
            var lavaAt = t;

            world.PositionOf[head] = new PinnaclePoint(0x586B0108, 211.5f, -110f, MatronData.FloorZ);
            fight.Tick(lavaAt + MatronScript.PillarSeconds - 0.2);
            Assert.AreEqual(0, world.Props.Count(x => x.Prop.Wcid == MatronData.EffectHostWcid && x.Prop.Script == MatronData.PillarScript), "no pillar before the fixed delay");

            var pillarAt = lavaAt + MatronScript.PillarSeconds + 0.05;
            fight.Tick(pillarAt);
            var pillars = world.Props.Where(x => x.Prop.Wcid == MatronData.EffectHostWcid && x.Prop.Script == MatronData.PillarScript).ToList();
            Assert.AreEqual(1, pillars.Count, "the pillar rises on her body");
            Assert.AreEqual((211.5f, -110f), (pillars[0].Prop.At.X, pillars[0].Prop.At.Y));
            Assert.AreEqual(0, world.Slain.Count, "the fire hits her before she dies");

            fight.Tick(pillarAt + MatronScript.SlaySeconds + 0.05);
            CollectionAssert.AreEqual(new[] { head }, world.Slain, "slain inside the pillar");
            fight.Tick(pillarAt + MatronScript.SlaySeconds + 0.2);   // her death is seen: the win
            Assert.AreEqual(PinnacleFightState.Won, fight.State);
            Assert.IsTrue(world.Spawns.Single(x => x.Wcid == MatronData.HeadWcid).LeavesCorpse, "the core spawns her to leave a corpse");
            Assert.IsFalse(pillars.Any(x => world.Destroyed.Contains(x.Result)), "the win keeps the pillars: only End destroys props");
        }
        [TestMethod]
        public void CoreRetry_RelaunchesTheFillWithItsOriginalRate_TheNextPollSendsTheCurrentOne()
        {
            var (fight, world, script, head) = Begun();

            world.AtFloor.Add(head);
            fight.Tick(1.1);   // threshold -> Running
            world.AtFloor.Remove(head);

            var pit = script.PitOrder[0];
            world.PositionOf[head] = MatronData.Opening(pit).Stand;
            fight.Tick(1.4);   // the walk poll (re-armed at 1.1 for 0.25 s): arrived -> Opening, the turn begins
            Assert.AreEqual(MatronStep.Opening, script.Step);
            fight.Tick(1.6);   // the face timer: the open motion
            fight.Tick(3.5);   // the open timer -> Flowing, the door open, the flow delay begins
            Assert.AreEqual(MatronStep.Flowing, script.Step);
            Assert.IsFalse(world.Hazards.Any(h => h.Kind == HazardMechanicRegistry.Fill), "no acid the instant the door opens");

            var t0 = 3.5 + script.Settings.FlowDelay;
            fight.Tick(t0);   // the flow-start timer: the fill starts at 0.03

            var first = world.Hazards.Last(h => h.Kind == HazardMechanicRegistry.Fill);
            Assert.AreEqual(0.03, first.Args.GetDouble("rate", 0, -2, 2), 1e-9);

            // seal: the run is told 0
            var door = world.Props.Last(x => x.Prop == MatronLayout.DoorProp(MatronData.Opening(pit))).Result;
            fight.OnObjectUsed(door);
            Assert.AreEqual(MatronStep.Sealed, script.Step);
            Assert.AreEqual((first.Token, "rate", 0.0), world.Controls.Last(c => c.Key == "rate"));

            // the run faults; the core retries it once next tick with the ORIGINAL args (rate 0.03)
            fight.OnHazardEnded(first.Token, HazardOutcome.Aborted);
            fight.Tick(t0 + 0.1);
            var relaunched = world.Hazards.Last(h => h.Kind == HazardMechanicRegistry.Fill);
            Assert.AreNotEqual(first.Token, relaunched.Token, "the core relaunched the run");
            Assert.AreEqual(0.03, relaunched.Args.GetDouble("rate", 0, -2, 2), 1e-9, "with its original rate: the bug's precondition");

            // the next poll sends the current rate to the new run
            fight.Tick(t0 + 1.5);
            Assert.IsTrue(world.Controls.Contains((relaunched.Token, "rate", 0.0)), "the sealed flow is told 0 again");
            StringAssert.Contains(fight.Describe(t0 + 1.5), " hazards=1 ", "one fill run live in the core");
        }
    }
}
