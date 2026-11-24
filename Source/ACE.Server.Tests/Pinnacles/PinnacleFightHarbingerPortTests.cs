using System.Linq;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The Harbinger port additions (hazard options, grounded owner, tagged add clear, bare cast, melee reach) against
    /// FakePinnacleWorld. No PropertyManager key is read, so nothing needs seeding.
    /// </summary>
    [TestClass]
    public class PinnacleFightHarbingerPortTests
    {
        private static readonly PinnaclePoint P = new PinnaclePoint(0x00690113, 10f, -20f, -47f);

        private sealed class NoopScript : IPinnacleBossScript
        {
            public string Kind => "test";

            public PinnacleScriptData Data { get; } = new PinnacleScriptData
            {
                Landblock = 0x0069,
                SealWcid = 800,
                SealPoints = new[] { new PinnaclePoint(0x00690113, 34f, -18f, -47f) },
                HeadWcid = 900,
                HeadPoint = new PinnaclePoint(0x00690113, 10.6544f, -20.161f, -47.1039f),
                HazardRegionPreset = "pinnacle",
            };

            public int PhaseCount => 5;

            public System.Collections.Generic.List<PinnacleEvent> Events { get; } = new System.Collections.Generic.List<PinnacleEvent>();

            public void OnStart(IPinnacleFightPort port) { }

            public void EnterPhase(int phase, IPinnacleFightPort port) { }

            public void OnEvent(PinnacleEvent e, IPinnacleFightPort port) => Events.Add(e);

            public void RestartPhase(IPinnacleFightPort port) { }

            public string Describe() => "noop";
        }

        private FakePinnacleWorld world;
        private NoopScript script;
        private PinnacleFight fight;
        private IPinnacleFightPort port;

        [TestInitialize]
        public void Setup()
        {
            world = new FakePinnacleWorld();
            script = new NoopScript();
            fight = new PinnacleFight(script, world, new PinnacleFightConfig { HoldSeconds = 600, ResumeGraceSeconds = 5 }, ownerGuid: 0x50000001, knotGuid: 0x80000002, instance: 7, knotLevel: 250, seed: 42);
            fight.Tick(0);
            Assert.IsTrue(fight.TryBegin(0));
            port = fight;
        }

        private double now;

        private void Tick(PinnacleFight f) => f.Tick(now += 1);

        [TestMethod]
        public void DestroyAdds_RemovesOnlyThatTag_NoAddDiedEver()
        {
            var a = port.SpawnAdd("summon-acid", 1, P, PinnacleTier.Elite);
            var b = port.SpawnAdd("summon-fire", 1, P, PinnacleTier.Elite);

            Assert.AreEqual(1, port.DestroyAdds("summon-acid"));
            Tick(fight);

            Assert.IsFalse(world.IsAlive(a));
            Assert.IsTrue(world.IsAlive(b));
            Assert.IsFalse(script.Events.Any(e => e.Kind == PinnacleEventKind.AddDied));
        }

        [TestMethod]
        public void StartHazard_CarriesOptionsToTheWorld_AndOnTheRetry()
        {
            var slow = new PinnacleHitSlow(0.25, 0.8, 4);
            world.RefuseNextHazards = 1;

            port.StartHazard("frost", "rain", null, HazardArgs.Empty, default, new PinnacleHazardOptions(slow));
            Tick(fight);

            Assert.AreEqual(slow, world.Hazards.Last().Options.HitSlow);
            Assert.AreEqual(2, world.Hazards.Count);
        }

        [TestMethod]
        public void CastAtOwnerNow_NoFaceNoBob_DroppedWhenHeld()
        {
            port.CastAtOwnerNow(port.Head, 4433);

            Assert.AreEqual(0, world.Calls.Count(c => c == "FaceOwner"));
            Assert.AreEqual(0, world.Calls.Count(c => c == "Bob"));
            Assert.AreEqual((port.Head, 4433u), world.Casts.Single());

            fight.OwnerDied(1);
            port.CastAtOwnerNow(port.Head, 4433);

            Assert.AreEqual(1, world.Casts.Count);
        }

        [TestMethod]
        public void IsOwnerGrounded_PassesThrough_FalseWhenEnded()
        {
            world.OwnerGrounded = true;
            Assert.IsTrue(port.IsOwnerGrounded());

            fight.Reset("t");
            Assert.IsFalse(port.IsOwnerGrounded());
        }

        [TestMethod]
        public void SetMeleeReach_PassesThrough()
        {
            port.SetMeleeReach(port.Head, 9.0f);

            Assert.AreEqual(9.0f, world.MeleeReach[port.Head]);
        }
    }
}
