using System.Linq;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Harbinger;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The Harbinger's hold/resume checkpoints and the final phase (plan T10, design 4.7) against the recording fake port.
    /// The core's hold stops every hazard and keeps every prop, add and the boss's health; RestartPhase decides what restarts.
    /// </summary>
    [TestClass]
    public class HarbingerRestartTests
    {
        private static readonly HarbingerSettings S = HarbingerSettings.Defaults;

        private static (HarbingerScript S, FakePinnacleFightPort P) Started()
        {
            var p = new FakePinnacleFightPort(1);
            var s = new HarbingerScript(S);
            s.OnStart(p);
            s.EnterPhase(1, p);
            return (s, p);
        }

        private static void Fire(HarbingerScript s, FakePinnacleFightPort p, string tag)
            => s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.TimerFired, Tag = tag }, p);

        private static PinnaclePoint Pt(float x, float y, float z) => new PinnaclePoint(0u, x, y, z);

        private static void ToAlcove(HarbingerScript s, FakePinnacleFightPort p)
        {
            p.AtFloor.Add(p.Head);
            Fire(s, p, "poll");
        }

        private static void ToLanding(HarbingerScript s, FakePinnacleFightPort p)
        {
            ToAlcove(s, p);
            var h = p.Spawns.Last(x => x.Tag == "crystal-acid").Result;
            p.Kill(h);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = "crystal-acid", Handle = h }, p);
        }

        private static int Count(FakePinnacleFightPort p, string call) => p.CallsNamed(call).Count();

        [TestMethod]
        public void RestartPhase_ReEngagesTheHead_InEveryStage()
        {
            foreach (var stage in new[] { HarbingerStage.Fighting, HarbingerStage.Alcove, HarbingerStage.Landing, HarbingerStage.Final })
            {
                var (s, p) = Started();

                if (stage == HarbingerStage.Alcove)
                    ToAlcove(s, p);
                else if (stage == HarbingerStage.Landing)
                    ToLanding(s, p);
                else if (stage == HarbingerStage.Final)
                    s.EnterPhase(5, p);

                Assert.AreEqual(stage, s.Stage);
                p.ClearRecordings();
                s.RestartPhase(p);

                CollectionAssert.Contains(p.Engaged, p.Head, stage.ToString());
                Assert.AreEqual(1, Count(p, "SetMeleeReach"), stage.ToString());
                Assert.AreEqual((float)S.MeleeReach, p.MeleeReach[p.Head], stage.ToString());
                Assert.IsTrue(p.TimerSets.Any(t => t.Tag == HarbingerScript.TimerPoll), stage.ToString());
                Assert.AreEqual(1, p.Said.Count(l => l == HarbingerLines.LineReturn), stage.ToString());
            }
        }

        [TestMethod]
        public void Restart_Fighting_AcidBackAtTheFloor_BossHealthUntouched()
        {
            var (s, p) = Started();
            p.HazardLevel["acid"] = -20.0;
            p.Now = 130;
            Fire(s, p, "poll");
            p.ClearRecordings();
            s.RestartPhase(p);

            Assert.AreEqual(0, Count(p, "SetHealthFloor"));
            Assert.AreEqual(0, p.Slain.Count);
            Assert.AreEqual(0, p.WinCount);
            Assert.IsFalse(p.Immune[p.Head]);
            Assert.AreEqual(-35.95, p.Hazards.Single(h => h.Tag == "acid").Args.GetDouble("start_z", 0, -100, 100), 1e-9);
        }

        [TestMethod]
        public void Restart_Alcove_RespawnsCrystalAndGuards_PortalKept()
        {
            var (s, p) = Started();
            ToAlcove(s, p);
            var oldCrystal = p.Spawns.Last(x => x.Wcid == 1008251).Result;
            p.ClearRecordings();
            s.RestartPhase(p);

            Assert.AreEqual(0, p.DestroyedProps.Count, "the portal stands");
            Assert.AreEqual(0, p.Props.Count, "and is not spawned twice");
            CollectionAssert.IsSubsetOf(new[] { "crystal-acid", "guard-acid" }, p.DestroyedAddTags);
            Assert.AreEqual(1, p.Spawns.Count(x => x.Wcid == 1008251));
            Assert.AreEqual(2, p.Spawns.Count(x => x.Wcid == 1008255));
            Assert.IsTrue(p.Spawns.All(x => x.Tier == PinnacleTier.Elite));
            Assert.IsTrue(p.Immune[p.Head]);
            Assert.AreEqual(HarbingerStage.Alcove, s.Stage);

            // the new crystal wakes on arrival
            var crystal = p.Spawns.Last(x => x.Wcid == 1008251).Result;
            Assert.AreNotEqual(oldCrystal, crystal);
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Acid);
            p.OwnerAt = Pt(alcove.Centre.X, alcove.Centre.Y, 0f);
            Fire(s, p, "poll");
            CollectionAssert.Contains(p.Engaged, crystal);

            // and its death ends the element as usual
            p.Kill(crystal);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = "crystal-acid", Handle = crystal }, p);
            Assert.AreEqual(HarbingerStage.Landing, s.Stage);
        }

        [TestMethod]
        public void Restart_Alcove_TheOldCrystalsDeath_IsStale_TheNewOnesEndsTheElement()
        {
            var (s, p) = Started();
            ToAlcove(s, p);
            var oldCrystal = p.Spawns.Last(x => x.Wcid == 1008251).Result;
            s.RestartPhase(p);
            var crystal = p.Spawns.Last(x => x.Wcid == 1008251).Result;
            Assert.AreNotEqual(oldCrystal, crystal);

            // a death report for the crystal the restart replaced ends nothing
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = "crystal-acid", Handle = oldCrystal }, p);
            Assert.AreEqual(HarbingerStage.Alcove, s.Stage, "the old crystal is stale");
            Assert.IsFalse(p.TimerSets.Any(t => t.Tag == HarbingerScript.TimerDrainDone), "and drains nothing");

            // the live crystal's death ends it
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = "crystal-acid", Handle = crystal }, p);
            Assert.AreEqual(HarbingerStage.Landing, s.Stage);
        }

        [TestMethod]
        public void Alcove_ACrystalSpawnFailure_SkipsTheAlcove()
        {
            var (s, p) = Started();
            ToAlcove(s, p);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = "crystal-acid", Handle = PinnacleHandle.None, SpawnFailed = true }, p);

            Assert.AreEqual(HarbingerStage.Landing, s.Stage);
        }

        [TestMethod]
        public void Restart_Alcove_PortalGone_IsRespawned()
        {
            var (s, p) = Started();
            ToAlcove(s, p);
            p.Kill(p.Props.Last(x => x.Prop.Wcid == 1008264).Result);
            p.ClearRecordings();
            s.RestartPhase(p);

            Assert.AreEqual(1, p.Props.Count(x => x.Prop.Wcid == 1008264));
            Assert.AreEqual(HarbingerStage.Alcove, s.Stage);
        }

        [TestMethod]
        public void Restart_Alcove_PortalRespawnFails_SkipsTheAlcove()
        {
            var (s, p) = Started();
            ToAlcove(s, p);
            p.Kill(p.Props.Last(x => x.Prop.Wcid == 1008264).Result);
            p.ClearRecordings();
            p.FailNextSpawns = 1;
            s.RestartPhase(p);

            Assert.AreEqual(HarbingerStage.Landing, s.Stage);
            Assert.IsTrue(p.Immune[p.Head]);

            p.OwnerGrounded = true;
            p.OwnerAt = Pt(10, -20, -36f);
            Fire(s, p, "poll");
            Assert.AreEqual(1, p.AdvanceCount);
        }

        [TestMethod]
        public void Restart_InLandingStage_Advances()
        {
            var (s, p) = Started();
            ToLanding(s, p);
            p.ClearRecordings();
            s.RestartPhase(p);

            Assert.AreEqual(1, p.AdvanceCount);
            Assert.IsTrue(p.Immune[p.Head]);
            Assert.AreEqual(0, p.Hazards.Count, "the element is over");
        }

        [TestMethod]
        public void Restart_InLandingStage_AdvancesAtMostOncePerPhase()
        {
            var (s, p) = Started();
            ToLanding(s, p);
            p.ClearRecordings();
            s.RestartPhase(p);
            s.RestartPhase(p);

            p.OwnerGrounded = true;
            p.OwnerAt = Pt(10, -20, -36f);
            Fire(s, p, "poll");

            Assert.AreEqual(1, p.AdvanceCount);
        }

        [TestMethod]
        public void Restart_Twice_StartsEachRunOnce()
        {
            var (s, p) = Started();
            p.ClearRecordings();
            s.RestartPhase(p);
            s.RestartPhase(p);

            Assert.AreEqual(2, p.Hazards.Count(h => h.Tag == "acid"));
            Assert.AreEqual(2, p.StoppedHazardTags.Count(t => t == "acid"));

            // each start is directly preceded by its stop
            var names = p.Calls.Where(c => c.Name == "StopHazard" || c.Name == "StartHazard").ToList();
            Assert.AreEqual(4, names.Count);
            Assert.IsTrue(names.Where((c, i) => i % 2 == 0).All(c => c.Name == "StopHazard"));
            Assert.IsTrue(names.Where((c, i) => i % 2 == 1).All(c => c.Name == "StartHazard"));
        }

        [TestMethod]
        public void Final_AllFourRunsReduced_NoImmunity_NoPortal_HeadDeathWins()
        {
            var (s, p) = Started();
            s.EnterPhase(5, p);
            p.ClearRecordings();
            s.RestartPhase(p);

            CollectionAssert.AreEquivalent(new[] { "acid", "lightning", "frost", "fire" }, p.Hazards.Select(h => h.Tag).ToList());
            Assert.AreEqual(-33.5, p.Hazards.Single(h => h.Tag == "acid").Args.GetDouble("max_z", 0, -100, 100), 1e-9);
            Assert.AreEqual(S.FinalAcidRate, p.Hazards.Single(h => h.Tag == "acid").Args.GetDouble("rate", 0, -5, 5), 1e-9);
            Assert.IsFalse(p.Immune[p.Head]);
            Assert.AreEqual(0, p.Props.Count);
            Assert.AreEqual(0, p.Spawns.Count(x => x.Tag.StartsWith("crystal-")));

            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = PinnacleEvent.HeadTag, Handle = p.Head }, p);
            Assert.AreEqual(1, p.WinCount);
        }

        [TestMethod]
        public void Final_SummonsOneOfEachKind_CapFour()
        {
            var (s, p) = Started();
            p.ClearRecordings();
            s.EnterPhase(5, p);

            CollectionAssert.AreEquivalent(new uint[] { 1008259, 1008260, 1008261, 1008262 }, p.Spawns.Select(x => x.Wcid).ToList());

            for (var i = 0; i < 3; i++)
                Fire(s, p, HarbingerScript.TimerSummon);

            Assert.AreEqual(4, p.Spawns.Count);
        }
    }
}
