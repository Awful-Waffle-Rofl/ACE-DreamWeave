using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The Harbinger round 2 core seams (Docs/Knots/HARBINGER-ROUND2-PLAN.md U1) through the real PinnacleFight against
    /// FakePinnacleWorld: the per-wcid health multiplier reaches IPinnacleWorld.Spawn for the head and for adds (and 1.0 for
    /// everything unlisted), per-run hazard options reach IPinnacleWorld.StartHazard unchanged (first try and retry), and
    /// GrantOwnerBuff casts from the head only while the fight runs. The core and the fakes read no PropertyManager key.
    /// </summary>
    [TestClass]
    public class PinnacleFightSeamTests
    {
        private const uint HeadWcid = 900;
        private const uint ListedAddWcid = 500;
        private const uint UnlistedAddWcid = 501;

        private static readonly PinnaclePoint P = new PinnaclePoint(0x00690113, 10f, -20f, -47f);

        private sealed class SeamScript : IPinnacleBossScript
        {
            public string Kind => "seam";

            public PinnacleScriptData Data { get; set; }

            public int PhaseCount => 3;

            public Action<int, IPinnacleFightPort> Entered;

            public void OnStart(IPinnacleFightPort port) { }

            public void EnterPhase(int phase, IPinnacleFightPort port) => Entered?.Invoke(phase, port);

            public void OnEvent(PinnacleEvent e, IPinnacleFightPort port) { }

            public void RestartPhase(IPinnacleFightPort port) { }

            public string Describe() => "seam script";
        }

        private static PinnacleScriptData Data(IReadOnlyDictionary<uint, double> multipliers) => new PinnacleScriptData
        {
            Landblock = 0x0069,
            HeadWcid = HeadWcid,
            HeadPoint = new PinnaclePoint(0x00690113, 10.6544f, -20.161f, -47.1039f),
            HazardRegionPreset = "pinnacle",
            HeadTier = PinnacleTier.Boss,
            HealthMultipliers = multipliers,
        };

        private static PinnacleFight Begun(FakePinnacleWorld world, SeamScript script)
        {
            var fight = new PinnacleFight(script, world, new PinnacleFightConfig { HoldSeconds = 600, ResumeGraceSeconds = 5 },
                ownerGuid: 0x50000001, knotGuid: 0x80000002, instance: 7, knotLevel: 250, seed: 42);
            fight.Tick(0);
            Assert.IsTrue(fight.TryBegin(0));
            return fight;
        }

        // ================================================================ health multipliers

        [TestMethod]
        public void HeadSpawn_ReceivesTheHeadWcidsMultiplierFromData()
        {
            var world = new FakePinnacleWorld();
            var script = new SeamScript { Data = Data(new Dictionary<uint, double> { { HeadWcid, 2.0 }, { ListedAddWcid, 3.0 } }) };

            Begun(world, script);

            var head = world.Spawns.Single(s => s.Wcid == HeadWcid);
            Assert.AreEqual(2.0, head.HealthMultiplier, "the head spawn carries Data.HealthMultipliers[HeadWcid]");
            Assert.AreEqual(PinnacleTier.Boss, head.Tier, "the multiplier rides on the tier, it does not replace it");
        }

        [TestMethod]
        public void AddSpawns_ReceiveTheirOwnMultiplier_AndUnlistedWcidsGetOne()
        {
            var world = new FakePinnacleWorld();
            var script = new SeamScript { Data = Data(new Dictionary<uint, double> { { ListedAddWcid, 2.0 } }) };
            script.Entered = (phase, port) =>
            {
                if (phase != 1)
                    return;

                port.SpawnAdd("listed", ListedAddWcid, P, PinnacleTier.Elite);
                port.SpawnAdd("unlisted", UnlistedAddWcid, P, PinnacleTier.Elite);
            };

            Begun(world, script);

            Assert.AreEqual(2.0, world.Spawns.Single(s => s.Wcid == ListedAddWcid).HealthMultiplier);
            Assert.AreEqual(1.0, world.Spawns.Single(s => s.Wcid == UnlistedAddWcid).HealthMultiplier, "a wcid the script does not list spawns at 1.0");
            Assert.AreEqual(1.0, world.Spawns.Single(s => s.Wcid == HeadWcid).HealthMultiplier, "the head is unlisted here: 1.0");
        }

        [TestMethod]
        public void AddSpawnRetry_KeepsTheMultiplier()
        {
            var world = new FakePinnacleWorld();
            var script = new SeamScript { Data = Data(new Dictionary<uint, double> { { ListedAddWcid, 2.0 } }) };
            script.Entered = (phase, port) =>
            {
                if (phase == 1)
                    port.SpawnAdd("listed", ListedAddWcid, P, PinnacleTier.Elite);
            };
            world.FailSpawnsOfWcid[ListedAddWcid] = 1;

            Begun(world, script);

            var tries = world.Spawns.Where(s => s.Wcid == ListedAddWcid).ToList();
            Assert.AreEqual(2, tries.Count, "the first spawn failed and was retried");
            Assert.IsTrue(tries.All(s => s.HealthMultiplier == 2.0), string.Join(", ", tries.Select(s => s.HealthMultiplier)));
        }

        [TestMethod]
        public void SealSpawns_ReceiveTheSealWcidsMultiplier_RetryIncluded()
        {
            const uint sealWcid = 800;
            var world = new FakePinnacleWorld();
            var data = new PinnacleScriptData
            {
                Landblock = 0x0069,
                HeadWcid = HeadWcid,
                HeadPoint = new PinnaclePoint(0x00690113, 10.6544f, -20.161f, -47.1039f),
                HazardRegionPreset = "pinnacle",
                SealWcid = sealWcid,
                SealPoints = new[] { new PinnaclePoint(0x00690113, 34f, -18f, -47f), new PinnaclePoint(0x00690113, 34f, -22f, -47f) },
                HealthMultipliers = new Dictionary<uint, double> { { sealWcid, 1.5 } },
            };
            world.FailSpawnsOfWcid[sealWcid] = 1;   // the first seal is retried

            Begun(world, new SeamScript { Data = data });

            var seals = world.Spawns.Where(s => s.Wcid == sealWcid).ToList();
            Assert.AreEqual(3, seals.Count, "two seals, the first retried once");
            Assert.IsTrue(seals.All(s => s.HealthMultiplier == 1.5), string.Join(", ", seals.Select(s => s.HealthMultiplier)));
            Assert.AreEqual(1.0, world.Spawns.Single(s => s.Wcid == HeadWcid).HealthMultiplier, "the head is unlisted");
        }

        [TestMethod]
        public void NoMultipliers_EverySpawnGetsOne()
        {
            var world = new FakePinnacleWorld();
            var data = Data(null);
            var script = new SeamScript { Data = data };
            script.Entered = (phase, port) =>
            {
                if (phase == 1)
                    port.SpawnAdd("add", ListedAddWcid, P, PinnacleTier.Elite);
            };

            Begun(world, script);

            Assert.IsTrue(world.Spawns.All(s => s.HealthMultiplier == 1.0));
            Assert.AreEqual(1.0, new PinnacleScriptData().HealthMultiplierFor(HeadWcid), "the default data lists nothing");
        }

        // ================================================================ per-run hazard options

        [TestMethod]
        public void StartHazard_HandsTheRegionAndAreaOptionsToTheWorld_OnTheFirstTryAndTheRetry()
        {
            var world = new FakePinnacleWorld();
            var area = new PinnacleHazardArea(18f, 20f, -2f, 0f, 0f);
            var byRegion = new PinnacleHazardOptions(Region: HazardRegionPresets.HarbingerAlcoveNorthName);
            var byArea = new PinnacleHazardOptions(Area: area);

            var script = new SeamScript { Data = Data(null) };
            script.Entered = (phase, port) =>
            {
                if (phase != 1)
                    return;

                port.StartHazard("region", "balls", null, HazardArgs.Empty, default, byRegion);
                port.StartHazard("area", "fill", null, HazardArgs.Empty, default, byArea);
            };

            world.RefuseNextHazards = 1;   // the "region" run is refused once and retried next tick
            var fight = Begun(world, script);
            fight.Tick(1);

            var region = world.Hazards.Where(h => h.Kind == "balls").ToList();
            Assert.AreEqual(2, region.Count, "refused, then retried");
            Assert.IsTrue(region.All(h => ReferenceEquals(h.Options, byRegion)), "both tries carry the same options");
            Assert.AreEqual(HazardRegionPresets.HarbingerAlcoveNorthName, region[1].Options.Region);

            var areaRun = world.Hazards.Single(h => h.Kind == "fill");
            Assert.AreSame(area, areaRun.Options.Area);
            Assert.IsNull(areaRun.Options.Region);
        }

        // ================================================================ owner buff

        [TestMethod]
        public void GrantOwnerBuff_Running_CastsFromTheHeadWithTheGivenDuration()
        {
            var world = new FakePinnacleWorld();
            var fight = Begun(world, new SeamScript { Data = Data(null) });

            Assert.IsTrue(fight.GrantOwnerBuff(6080, 120));

            Assert.AreEqual(1, world.OwnerBuffs.Count);
            Assert.AreEqual(fight.Head, world.OwnerBuffs[0].Caster, "the head is the caster");
            Assert.AreEqual(6080u, world.OwnerBuffs[0].SpellId);
            Assert.AreEqual(120.0, world.OwnerBuffs[0].Seconds);
        }

        [TestMethod]
        public void GrantOwnerBuff_ReturnsTheWorldsAnswer()
        {
            var world = new FakePinnacleWorld { GrantOwnerBuffResult = false };
            var fight = Begun(world, new SeamScript { Data = Data(null) });

            Assert.IsFalse(fight.GrantOwnerBuff(6080, 120));
            Assert.AreEqual(1, world.OwnerBuffs.Count, "asked, and the world said no");
        }

        [TestMethod]
        public void GrantOwnerBuff_IsDropped_WithADeadHead_ABadDuration_OrBeforeTheFightBegins()
        {
            var world = new FakePinnacleWorld();
            var notBegun = new PinnacleFight(new SeamScript { Data = Data(null) }, world, new PinnacleFightConfig(),
                ownerGuid: 0x50000001, knotGuid: 0x80000002, instance: 7, knotLevel: 250, seed: 42);
            notBegun.Tick(0);

            Assert.IsFalse(notBegun.GrantOwnerBuff(6080, 120), "not Running yet");

            var fight = Begun(world, new SeamScript { Data = Data(null) });

            Assert.IsFalse(fight.GrantOwnerBuff(6080, 0), "zero seconds");
            Assert.IsFalse(fight.GrantOwnerBuff(6080, -5), "negative seconds");
            Assert.IsFalse(fight.GrantOwnerBuff(6080, double.NaN), "NaN");
            Assert.IsFalse(fight.GrantOwnerBuff(6080, double.PositiveInfinity), "infinity");

            world.Kill(fight.Head);
            Assert.IsFalse(fight.GrantOwnerBuff(6080, 120), "dead head");

            Assert.AreEqual(0, world.OwnerBuffs.Count, "nothing reached the world");
        }
    }
}
