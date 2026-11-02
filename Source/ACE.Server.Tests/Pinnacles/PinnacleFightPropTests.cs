using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The fight-scoped prop API of PinnacleFight (props registry, ObjectUsed, tag-routed hazard steering, admin entry)
    /// against FakePinnacleWorld. No PropertyManager key is read by the core or the fakes, so nothing needs seeding.
    /// </summary>
    [TestClass]
    public class PinnacleFightPropTests
    {
        private const uint HeadWcid = 900;
        private const uint PropWcid = 1006967;

        private static readonly PinnaclePoint P = new PinnaclePoint(0x00690113, 10f, -20f, -47f);

        private sealed class StubScript : IPinnacleBossScript
        {
            public string Kind => "test";

            public PinnacleScriptData Data { get; } = new PinnacleScriptData
            {
                Landblock = 0x0069,
                HeadWcid = HeadWcid,
                HeadPoint = new PinnaclePoint(0x00690113, 10.6544f, -20.161f, -47.1039f),
                HazardRegionPreset = "pinnacle",
            };

            public int PhaseCount { get; set; } = 2;

            public List<PinnacleEvent> Events { get; } = new List<PinnacleEvent>();

            public Action<int, IPinnacleFightPort> Entered;
            public Action<PinnacleEvent, IPinnacleFightPort> Evented;

            public void OnStart(IPinnacleFightPort port) { }

            public void EnterPhase(int phase, IPinnacleFightPort port) => Entered?.Invoke(phase, port);

            public void OnEvent(PinnacleEvent e, IPinnacleFightPort port)
            {
                Events.Add(e);
                Evented?.Invoke(e, port);
            }

            public void RestartPhase(IPinnacleFightPort port) { }

            public string Describe() => "stub";
        }

        private static PinnacleFightConfig Cfg => new PinnacleFightConfig { HoldSeconds = 600, ResumeGraceSeconds = 5 };

        private static PinnacleFight NewFight(FakePinnacleWorld world, StubScript script)
            => new PinnacleFight(script, world, Cfg, ownerGuid: 0x50000001, knotGuid: 0x80000002, instance: 7, knotLevel: 250, seed: 42);

        private static PinnacleFight Begun(FakePinnacleWorld world, StubScript script)
        {
            var fight = NewFight(world, script);
            fight.Tick(0);
            Assert.IsTrue(fight.TryBegin(0));
            return fight;
        }

        // ---------------------------------------------------------------- props

        [TestMethod]
        public void Prop_SurvivesAPhaseChange_AndIsDestroyedAtEnd()
        {
            var world = new FakePinnacleWorld();
            var script = new StubScript();
            var h = PinnacleHandle.None;
            script.Entered = (n, port) =>
            {
                if (n == 1)
                {
                    h = port.SpawnProp("p", new PinnacleProp(PropWcid, P));
                    port.SetTimer("adv", 0);
                }
            };
            script.Evented = (e, port) =>
            {
                if (e.Kind == PinnacleEventKind.TimerFired && e.Tag == "adv")
                    port.AdvancePhase();
            };

            var fight = Begun(world, script);
            Assert.IsFalse(h.IsNone);

            fight.Tick(1);

            Assert.AreEqual(2, fight.Phase, "the phase advanced");
            Assert.IsTrue(world.IsAlive(h), "ClearPhase leaves a prop");

            fight.Reset("t");

            Assert.IsFalse(world.IsAlive(h), "End destroys every prop");
            Assert.AreEqual(1, world.Destroyed.Count(x => x == h));
        }

        [TestMethod]
        public void Prop_Destroyed_NeverRaisesAddDied()
        {
            var world = new FakePinnacleWorld();
            var script = new StubScript();
            var h = PinnacleHandle.None;
            script.Entered = (n, port) => h = port.SpawnProp("p", new PinnacleProp(PropWcid, P));

            var fight = Begun(world, script);

            world.Kill(h);
            fight.Tick(1);
            fight.Tick(2);

            Assert.IsFalse(script.Events.Any(e => e.Kind == PinnacleEventKind.AddDied), "a prop dying is not an add dying");
        }

        [TestMethod]
        public void SpawnProp_RetriesOnce_AndRaisesNothingWhenItFailsTwice()
        {
            var world = new FakePinnacleWorld { FailNextProps = 1 };
            var script = new StubScript();
            var first = PinnacleHandle.None;
            script.Entered = (n, port) => first = port.SpawnProp("p", new PinnacleProp(PropWcid, P));

            var fight = Begun(world, script);

            Assert.IsFalse(first.IsNone, "the retry succeeded");
            Assert.AreEqual(2, world.Props.Count);

            var world2 = new FakePinnacleWorld { FailNextProps = 2 };
            var script2 = new StubScript();
            var second = new PinnacleHandle(99);
            script2.Entered = (n, port) => second = port.SpawnProp("p", new PinnacleProp(PropWcid, P));

            var fight2 = Begun(world2, script2);
            fight2.Tick(1);

            Assert.IsTrue(second.IsNone);
            Assert.AreEqual(0, script2.Events.Count, "no synthesised AddDied");
            Assert.AreEqual(1, world2.Errors.Count);
        }

        [TestMethod]
        public void Hold_NeverPacifiesOrClampsAProp()
        {
            var world = new FakePinnacleWorld();
            var script = new StubScript();
            var h = PinnacleHandle.None;
            script.Entered = (n, port) => h = port.SpawnProp("p", new PinnacleProp(PropWcid, P));

            var fight = Begun(world, script);
            var head = fight.Head;

            fight.OwnerDied(1);
            world.OwnerPresent = false;
            fight.Tick(2);

            Assert.AreEqual(PinnacleFightState.Held, fight.State);
            Assert.IsTrue(world.Peaceful.TryGetValue(head, out var headPeaceful) && headPeaceful, "positive control: the head is pacified");
            Assert.IsFalse(world.Peaceful.ContainsKey(h), "the prop is never pacified");
            Assert.IsFalse(world.Clamps.Any(c => c.Handle == h), "the prop is never health-clamped");
            Assert.IsTrue(world.Clamps.Any(c => c.Handle == head), "positive control: the head is clamped");
        }

        // ---------------------------------------------------------------- ObjectUsed

        [TestMethod]
        public void ObjectUsed_IsDeliveredAtOnceWhileRunning()
        {
            var world = new FakePinnacleWorld();
            var script = new StubScript();
            var h = PinnacleHandle.None;
            script.Entered = (n, port) => h = port.SpawnProp("door-1", new PinnacleProp(PropWcid, P));

            var fight = Begun(world, script);

            fight.OnObjectUsed(h);

            Assert.AreEqual(1, script.Events.Count(e => e.Kind == PinnacleEventKind.ObjectUsed));
            Assert.AreEqual(PinnacleEventKind.ObjectUsed, script.Events.Last().Kind);
            Assert.AreEqual("door-1", script.Events.Last().Tag);
            Assert.AreEqual(h, script.Events.Last().Handle);
        }

        [TestMethod]
        public void ObjectUsed_UntrackedHandleNoneOrEnded_IsIgnored()
        {
            var world = new FakePinnacleWorld();
            var script = new StubScript();
            PinnacleHandle h = PinnacleHandle.None;
            script.Entered = (n, port) => h = port.SpawnProp("door-1", new PinnacleProp(PropWcid, P));

            var fight = Begun(world, script);

            fight.OnObjectUsed(PinnacleHandle.None);
            fight.OnObjectUsed(fight.Head);   // a live handle, but not a prop
            fight.OnObjectUsed(new PinnacleHandle(777));

            Assert.AreEqual(0, script.Events.Count);

            fight.Reset("t");
            fight.OnObjectUsed(h);

            Assert.AreEqual(0, script.Events.Count);
        }

        [TestMethod]
        public void ObjectUsed_WhileHeld_IsIgnored()
        {
            var world = new FakePinnacleWorld();
            var script = new StubScript();
            var h = PinnacleHandle.None;
            script.Entered = (n, port) => h = port.SpawnProp("door-1", new PinnacleProp(PropWcid, P));

            var fight = Begun(world, script);

            fight.OwnerDied(1);
            world.OwnerPresent = false;
            Assert.AreEqual(PinnacleFightState.Held, fight.State);

            fight.OnObjectUsed(h);

            fight.ArmResume();
            world.OwnerPresent = true;
            fight.Tick(2);
            fight.Tick(7);

            Assert.AreEqual(PinnacleFightState.Running, fight.State, "resumed after the grace");
            Assert.IsFalse(script.Events.Any(e => e.Kind == PinnacleEventKind.ObjectUsed), "not delivered, not queued for the resume");
        }

        // ---------------------------------------------------------------- hazard steering

        [TestMethod]
        public void ControlHazard_RoutesToTheTaggedRunOnly()
        {
            var world = new FakePinnacleWorld();
            var script = new StubScript();
            var fight = Begun(world, script);
            IPinnacleFightPort port = fight;

            port.StartHazard("fill", "fill", null, HazardArgs.Empty);
            port.StartHazard("other", "wisps", null, HazardArgs.Empty);

            var fillToken = world.Hazards[0].Token;
            Assert.IsTrue(fillToken > 0);
            Assert.AreNotEqual(fillToken, world.Hazards[1].Token);

            Assert.IsTrue(port.ControlHazard("fill", "rate", 0.03));
            Assert.AreEqual((fillToken, "rate", 0.03), world.Controls.Single());

            Assert.IsFalse(port.ControlHazard("none", "rate", 1));
            Assert.AreEqual(1, world.Controls.Count, "an unknown tag reaches the world not at all");

            Assert.IsFalse(port.ControlHazard(null, "rate", 1));
            port.ControlHazard("fill", "rate", 0.5);
            Assert.AreEqual(2, world.Controls.Count);
            Assert.IsTrue(world.Controls.All(c => c.Token == fillToken));
        }

        [TestMethod]
        public void TryGetHazardLevel_ReadsTheTaggedRun()
        {
            var world = new FakePinnacleWorld();
            var script = new StubScript();
            var fight = Begun(world, script);
            IPinnacleFightPort port = fight;

            port.StartHazard("fill", "fill", null, HazardArgs.Empty);
            port.StartHazard("other", "wisps", null, HazardArgs.Empty);

            world.HazardLevel[world.Hazards[0].Token] = -58.5;

            Assert.IsTrue(port.TryGetHazardLevel("fill", out var z));
            Assert.AreEqual(-58.5, z);

            Assert.IsFalse(port.TryGetHazardLevel("other", out var none), "a run with no level reads false");
            Assert.AreEqual(0.0, none);
            Assert.IsFalse(port.TryGetHazardLevel("none", out _));
        }

        [TestMethod]
        public void PassThroughs_IgnoreNoneAndAnEndedFight()
        {
            var world = new FakePinnacleWorld();
            var script = new StubScript();
            var fight = Begun(world, script);
            IPinnacleFightPort port = fight;

            port.StartHazard("fill", "fill", null, HazardArgs.Empty);
            world.OwnerAt = P;

            port.SetDoorOpen(PinnacleHandle.None, true);
            port.Engage(PinnacleHandle.None);
            port.Slay(PinnacleHandle.None);
            port.SetHealthFloor(PinnacleHandle.None, 0.5);
            port.MoveTo(PinnacleHandle.None, P);
            Assert.IsFalse(port.IsAtHealthFloor(PinnacleHandle.None));
            Assert.IsFalse(port.TryGetPosition(PinnacleHandle.None, out _));

            Assert.AreEqual(0, world.Engaged.Count + world.Slain.Count + world.Floors.Count + world.MoveTos.Count + world.DoorOpen.Count);
            Assert.IsTrue(port.TryGetOwnerPosition(out var at), "positive control: a live fight reads the owner");
            Assert.AreEqual(P, at);

            fight.Reset("t");

            Assert.IsFalse(port.TryGetOwnerPosition(out _));
            Assert.IsFalse(port.ControlHazard("fill", "rate", 1));
            Assert.IsFalse(port.TryGetHazardLevel("fill", out _));
            Assert.IsFalse(port.SpawnProp("late", new PinnacleProp(PropWcid, P)) != PinnacleHandle.None);
        }

        // ---------------------------------------------------------------- admin entry

        [TestMethod]
        public void AdminEntry_BeginsUncharged_AndNeverReportsAClear()
        {
            // positive control first: the same script, entered normally, charges and reports a clear
            var normalWorld = new FakePinnacleWorld();
            var normal = NewFight(normalWorld, WinningScript());
            normal.Tick(0);
            Assert.IsTrue(normal.TryBegin(0));
            normal.Tick(1);
            Assert.AreEqual(1, normalWorld.Charges);
            Assert.AreEqual(1, normalWorld.Clears.Count, "control: a normal win reports its clear");

            var world = new FakePinnacleWorld();
            var fight = NewFight(world, WinningScript());
            fight.MarkAdminEntry();
            fight.Tick(0);
            Assert.IsTrue(fight.TryBegin(0));
            fight.Tick(1);

            Assert.AreEqual(PinnacleFightState.Won, fight.State);
            Assert.AreEqual(0, world.Charges, "no charge for an admin entry");
            Assert.AreEqual(0, world.Clears.Count, "an admin entry never reports a clear");
            Assert.AreEqual(1, world.DeliverAttempts, "the cache still delivers");

            fight.Reset("t");
            Assert.AreEqual(0, world.AttemptLostReports, "and a lost admin entry never reports a loss");
        }

        private static StubScript WinningScript()
        {
            var s = new StubScript();
            s.Entered = (n, port) => port.Win();
            return s;
        }
    }
}
