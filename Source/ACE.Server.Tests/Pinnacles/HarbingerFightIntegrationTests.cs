using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Harbinger;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The REAL PinnacleFight driving the REAL HarbingerScript on FakePinnacleWorld (final review I2, in the shape of
    /// MatronFightIntegrationTests). Pins what the recording-port tests cannot: the core's hold stops every run and keeps the
    /// adds and props, its resume drains queued events and runs RestartPhase, and it applies an AdvancePhase asked for inside
    /// the restart. A death and resume in each stage (Fighting, Alcove, Landing, Final) must leave exactly one live run per
    /// wanted element. Reads no PropertyManager value (the script reads the clamp from its settings record).
    /// </summary>
    [TestClass]
    public class HarbingerFightIntegrationTests
    {
        private static PinnacleFightConfig Cfg => new PinnacleFightConfig { HoldSeconds = 600, ResumeGraceSeconds = 5 };

        private static readonly HarbingerElementDef Acid = HarbingerData.Element(HarbingerElement.Acid);

        private static readonly HarbingerAlcove AcidAlcove = HarbingerData.AlcoveOf(HarbingerElement.Acid);

        private static (PinnacleFight Fight, FakePinnacleWorld World, HarbingerScript Script, PinnacleHandle Head) Begun()
        {
            var world = new FakePinnacleWorld();
            var script = new HarbingerScript();
            var fight = new PinnacleFight(script, world, Cfg, ownerGuid: 0x50000001, knotGuid: 0x80000002, instance: 7, knotLevel: 250, seed: 42);
            fight.Tick(0);
            Assert.IsTrue(fight.TryBegin(0));
            Assert.AreEqual(PinnacleFightState.Running, fight.State);
            return (fight, world, script, world.HandleOf(HarbingerData.HeadWcid));
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

        /// <summary>The runs of <paramref name="kind"/> the world still has live (started, never stopped).</summary>
        private static int Live(FakePinnacleWorld world, string kind)
            => world.Hazards.Count(h => h.Kind == kind && h.Token > 0 && !world.StoppedTokens.Contains(h.Token));

        private static int LiveOf(FakePinnacleWorld world, uint wcid)
            => world.Spawns.Count(s => s.Wcid == wcid && !s.Result.IsNone && world.IsAlive(s.Result));

        private static int LivePropsOf(FakePinnacleWorld world, uint wcid)
            => world.Props.Count(p => p.Prop.Wcid == wcid && !p.Result.IsNone && world.IsAlive(p.Result));

        /// <summary>Phase 1 to its health floor: the next poll turns him immune and opens the acid alcove.</summary>
        private static void ToAlcove(PinnacleFight fight, FakePinnacleWorld world, HarbingerScript script, PinnacleHandle head)
        {
            world.AtFloor.Add(head);
            fight.Tick(1.1);
            world.AtFloor.Remove(head);
            Assert.AreEqual(HarbingerStage.Alcove, script.Stage);
            Assert.AreEqual(1, LivePropsOf(world, AcidAlcove.PortalWcid));
        }

        /// <summary>
        /// The frost alcove's Whiteout runs the world has live (rain on the south alcove preset): started, never stopped, and
        /// not one this test ended itself (<paramref name="ended"/>; the fake world does not mark a run it reported ended).
        /// </summary>
        private static List<FakePinnacleWorld.HazardCall> LiveWhiteout(FakePinnacleWorld world, ICollection<int> ended = null)
            => world.Hazards.Where(h => h.Kind == HazardMechanicRegistry.Rain && h.Options?.Region == HazardRegionPresets.HarbingerAlcoveSouthName
                && h.Token > 0 && !world.StoppedTokens.Contains(h.Token) && (ended == null || !ended.Contains(h.Token))).ToList();

        /// <summary>Phase 3 (frost) at its threshold, the owner arrived in the frost alcove at t = 3.1: one Whiteout run is live.</summary>
        private static (PinnacleFight Fight, FakePinnacleWorld World, HarbingerScript Script) FrostArrived(double pressureSeconds)
        {
            var world = new FakePinnacleWorld();
            var script = new HarbingerScript(HarbingerSettings.Defaults with { PressureSeconds = pressureSeconds, AlcoveGraceSeconds = 0 });   // round 4: no grace, these tests race the pressure against a live Whiteout
            var fight = new PinnacleFight(script, world, Cfg, ownerGuid: 0x50000001, knotGuid: 0x80000002, instance: 7, knotLevel: 250, seed: 42);
            fight.Tick(0);
            Assert.IsTrue(fight.TryBegin(0));
            var head = world.HandleOf(HarbingerData.HeadWcid);

            fight.JumpToPhase(3, 0.5);
            fight.Tick(1.0);
            Assert.AreEqual(3, fight.Phase);

            world.OwnerAt = new PinnaclePoint(0u, 10f, -20f, -36f);
            world.AtFloor.Add(head);
            fight.Tick(2.1);
            world.AtFloor.Remove(head);
            Assert.AreEqual(HarbingerStage.Alcove, script.Stage);

            var c = HarbingerData.AlcoveOf(HarbingerElement.Frost).Centre;
            world.OwnerAt = new PinnaclePoint(0u, c.X, c.Y, 0f);
            fight.Tick(3.1);
            Assert.AreEqual(1, LiveWhiteout(world).Count, "the arrival started one Whiteout");
            return (fight, world, script);
        }

        /// <summary>
        /// Review F6 on f60469ed9: the Whiteout run ends (its HazardEnded is queued, or its retry is due) in the same tick the
        /// doorway pressure fires. The core drains the end first (restart), then the pressure's Escalate stops that restart and
        /// starts the pressed run: exactly one Whiteout is live, and it is the pressed one.
        /// </summary>
        [TestMethod]
        public void Pressure_InTheSameTickAsAQueuedWhiteoutEnd_LeavesExactlyOneLiveWhiteout()
        {
            foreach (var outcome in new[] { HazardOutcome.Completed, HazardOutcome.Aborted, HazardOutcome.Stopped })
            {
                var (fight, world, script) = FrostArrived(pressureSeconds: 5);
                var first = LiveWhiteout(world).Single();

                var ended = new List<int> { first.Token };
                fight.OnHazardEnded(first.Token, outcome);
                fight.Tick(9.0);   // the pressure (armed at 3.1 for 5 s) is due in this same tick

                var live = LiveWhiteout(world, ended);
                Assert.AreEqual(1, live.Count, $"{outcome}: one live Whiteout, got tokens {string.Join(",", live.Select(h => h.Token))}");
                Assert.AreEqual(HarbingerSettings.Defaults.WhiteoutPressureMaxFalling, live[0].Args.GetInt("max_falling", 0, 0, 99), $"{outcome}: the pressed run");
                StringAssert.Contains(script.Describe(), "sig=pressed");

                // later ticks change nothing
                fight.Tick(10.0);
                fight.Tick(12.0);
                Assert.AreEqual(1, LiveWhiteout(world, ended).Count, outcome.ToString());

                // control: the end WAS acted on (a restart or the core's retry was started, then stopped by the pressure)
                Assert.IsTrue(world.Hazards.Count(h => h.Options?.Region == HazardRegionPresets.HarbingerAlcoveSouthName) >= 3, $"{outcome}: the end was drained before the pressure");
            }
        }

        /// <summary>The other order: the pressure restarts the run first, and the OLD run's end arrives after it, late. The core forgot the old token at the stop, so the end is ignored.</summary>
        [TestMethod]
        public void Pressure_ThenALateEndOfTheOldWhiteout_LeavesExactlyOneLiveWhiteout()
        {
            var (fight, world, _) = FrostArrived(pressureSeconds: 5);
            var first = LiveWhiteout(world).Single();

            fight.Tick(9.0);
            var pressed = LiveWhiteout(world).Single();
            Assert.AreNotEqual(first.Token, pressed.Token);

            fight.OnHazardEnded(first.Token, HazardOutcome.Completed);
            fight.Tick(10.0);
            fight.Tick(11.0);

            Assert.AreEqual(pressed.Token, LiveWhiteout(world).Single().Token);
            Assert.AreEqual(2, world.Hazards.Count(h => h.Options?.Region == HazardRegionPresets.HarbingerAlcoveSouthName), "the late end restarted nothing: the first run and the pressed one");
        }

        [TestMethod]
        public void Death_InFighting_ResumesWithOneAcidRun_HeadNotImmune()
        {
            var (fight, world, script, head) = Begun();
            fight.Tick(1.1);
            Assert.AreEqual(1, Live(world, HazardMechanicRegistry.Fill));

            DieAndReturn(fight, world, 2.0);

            Assert.AreEqual(HarbingerStage.Fighting, script.Stage);
            Assert.AreEqual(1, fight.Phase);
            Assert.AreEqual(1, Live(world, HazardMechanicRegistry.Fill), "exactly one acid run after the resume");
            Assert.AreEqual(0, Live(world, HazardMechanicRegistry.Wisps) + Live(world, HazardMechanicRegistry.Rain) + Live(world, HazardMechanicRegistry.Balls));
            Assert.IsFalse(world.Immune[head]);
            Assert.AreEqual(HarbingerSettings.AcidFloorZ, world.Hazards.Last(h => h.Kind == HazardMechanicRegistry.Fill).Args.GetDouble("start_z", 0, -100, 100), 1e-9);
        }

        // ---- round 3, owner 2026-11-19: "ensure the acid level resets if the player dies during any phase where acid was rising" ----

        private static FakePinnacleWorld.HazardCall LastAcid(FakePinnacleWorld world) => world.Hazards.Last(h => h.Kind == HazardMechanicRegistry.Fill);

        private static double StartZ(FakePinnacleWorld.HazardCall run) => run.Args.GetDouble("start_z", 0, -100, 100);

        private static double Rate(FakePinnacleWorld.HazardCall run) => run.Args.GetDouble("rate", 0, -10, 10);

        /// <summary>
        /// The acid of the live fight has risen to <paramref name="level"/> (the world reports it) and the clock runs to t 61.5,
        /// past the first rate doubling (60 s): the poll reads the level and, outside the final, sends the doubled rate.
        /// </summary>
        private static void AcidRisesPastAnEscalation(PinnacleFight fight, FakePinnacleWorld world, double level, bool final)
        {
            var acid = LastAcid(world);
            world.HazardLevel[acid.Token] = level;
            fight.Tick(61.5);

            if (!final)
                Assert.IsTrue(world.Controls.Any(c => c.Token == acid.Token && c.Key == "rate" && Math.Abs(c.Value - 2 * HarbingerSettings.Defaults.AcidRate) < 1e-12),
                    "control: the acid escalated (rate doubled) before the death");
        }

        /// <summary>
        /// Phase 1 Fighting, phase 1 Alcove (the acid still rises while the owner is up) and the final: the acid has risen well
        /// above its floor and escalated, the owner dies, and the resumed run starts at the floor at the phase's base rate.
        /// </summary>
        [TestMethod]
        public void Death_WhileTheAcidRises_ResumesItsRunAtTheFloor_AtTheBaseRate_InP1FightingP1AlcoveAndTheFinal()
        {
            foreach (var where in new[] { "p1-fighting", "p1-alcove", "p5" })
            {
                var (fight, world, script, head) = Begun();
                var final = where == "p5";

                if (where == "p1-fighting")
                    fight.Tick(1.1);
                else if (where == "p1-alcove")
                    ToAlcove(fight, world, script, head);
                else
                {
                    fight.JumpToPhase(5, 0.5);
                    fight.Tick(1.1);
                    Assert.AreEqual(HarbingerStage.Final, script.Stage);
                }

                var risen = final ? -34.0 : -30.0;
                AcidRisesPastAnEscalation(fight, world, risen, final);
                var before = LastAcid(world);

                DieAndReturn(fight, world, 62.0);

                var after = LastAcid(world);
                Assert.AreNotEqual(before.Token, after.Token, $"{where}: the resume started a new acid run");
                Assert.AreEqual(1, Live(world, HazardMechanicRegistry.Fill), $"{where}: exactly one acid run");
                Assert.AreEqual(HarbingerSettings.AcidFloorZ, StartZ(after), 1e-9, $"{where}: the acid starts at the floor, not at {risen}");
                Assert.AreEqual(final ? HarbingerSettings.Defaults.FinalAcidRate : HarbingerSettings.Defaults.AcidRate, Rate(after), 1e-12, $"{where}: the base rate");
            }
        }

        /// <summary>
        /// Control for the test above: the same risen, escalated acid restarted WITHOUT a death (its run ends Completed) starts
        /// from where it was, at the escalated rate. So the reset at the resume is what puts it back at the floor.
        /// </summary>
        [TestMethod]
        public void AcidRunRestart_WithoutADeath_KeepsTheRisenLevelAndTheEscalatedRate()
        {
            foreach (var final in new[] { false, true })
            {
                var (fight, world, script, _) = Begun();

                if (final)
                {
                    fight.JumpToPhase(5, 0.5);
                    fight.Tick(1.1);
                }
                else
                {
                    fight.Tick(1.1);
                }

                var risen = final ? -34.0 : -30.0;
                AcidRisesPastAnEscalation(fight, world, risen, final);

                fight.OnHazardEnded(LastAcid(world).Token, HazardOutcome.Completed);
                fight.Tick(62.0);

                var after = LastAcid(world);
                Assert.AreEqual(risen, StartZ(after), 1e-9, $"final={final}: a restart keeps the level");
                Assert.AreEqual(final ? HarbingerSettings.Defaults.FinalAcidRate : 2 * HarbingerSettings.Defaults.AcidRate, Rate(after), 1e-12, $"final={final}");
            }
        }

        [TestMethod]
        public void Death_InAlcove_RespawnsOneCrystalAndTwoGuards_KeepsThePortal_OneAcidRun()
        {
            var (fight, world, script, head) = Begun();
            ToAlcove(fight, world, script, head);
            var portal = world.Props.Single(p => p.Prop.Wcid == AcidAlcove.PortalWcid).Result;
            var oldCrystal = world.HandleOf(Acid.CrystalWcid);

            DieAndReturn(fight, world, 2.0);

            Assert.AreEqual(HarbingerStage.Alcove, script.Stage);
            Assert.AreEqual(1, LiveOf(world, Acid.CrystalWcid), "one crystal");
            Assert.AreNotEqual(oldCrystal, world.HandleOf(Acid.CrystalWcid), "a fresh one");
            Assert.AreEqual(2, LiveOf(world, Acid.GuardianWcid), "two guardians");
            Assert.AreEqual(1, world.Props.Count(p => p.Prop.Wcid == AcidAlcove.PortalWcid), "the portal is never spawned twice");
            Assert.IsTrue(world.IsAlive(portal), "the portal kept");
            Assert.IsTrue(world.Immune[head]);
            Assert.AreEqual(1, Live(world, HazardMechanicRegistry.Fill), "exactly one acid run after the resume");
        }

        [TestMethod]
        public void TwoDeaths_InAlcove_StillOneCrystalTwoGuardsOnePortal()
        {
            var (fight, world, script, head) = Begun();
            ToAlcove(fight, world, script, head);

            var t = DieAndReturn(fight, world, 2.0);
            DieAndReturn(fight, world, t + 1.0);

            Assert.AreEqual(HarbingerStage.Alcove, script.Stage);
            Assert.AreEqual(1, LiveOf(world, Acid.CrystalWcid));
            Assert.AreEqual(2, LiveOf(world, Acid.GuardianWcid));
            Assert.AreEqual(1, world.Props.Count(p => p.Prop.Wcid == AcidAlcove.PortalWcid));
            Assert.AreEqual(1, LivePropsOf(world, AcidAlcove.PortalWcid));
            Assert.AreEqual(1, Live(world, HazardMechanicRegistry.Fill));
        }

        [TestMethod]
        public void Death_InAlcove_TheNewCrystalsDeath_EndsTheElement()
        {
            var (fight, world, script, head) = Begun();
            ToAlcove(fight, world, script, head);
            var t = DieAndReturn(fight, world, 2.0);

            world.Kill(world.HandleOf(Acid.CrystalWcid));
            fight.Tick(t + 0.2);

            Assert.AreEqual(HarbingerStage.Landing, script.Stage);
            Assert.AreEqual(0, LivePropsOf(world, AcidAlcove.PortalWcid), "the portal goes with the crystal");
            Assert.AreEqual(0, LiveOf(world, Acid.GuardianWcid));
        }

        [TestMethod]
        public void Death_InLanding_AdvancesExactlyOnce_ToOneLightningRun()
        {
            var (fight, world, script, head) = Begun();
            ToAlcove(fight, world, script, head);

            world.Kill(world.HandleOf(Acid.CrystalWcid));
            fight.Tick(1.3);
            Assert.AreEqual(HarbingerStage.Landing, script.Stage);
            Assert.AreEqual(1, fight.Phase);

            var t = DieAndReturn(fight, world, 2.0);

            Assert.AreEqual(2, fight.Phase, "the next phase starts on return");
            Assert.AreEqual(HarbingerStage.Fighting, script.Stage);
            Assert.AreEqual(1, Live(world, HazardMechanicRegistry.Wisps), "exactly one lightning run");
            Assert.AreEqual(0, Live(world, HazardMechanicRegistry.Fill), "the acid is over");
            Assert.IsFalse(world.Immune[head]);

            // the owner standing on the floor afterwards never asks for another phase
            world.OwnerGrounded = true;
            world.OwnerAt = new PinnaclePoint(0x00BC0108, 28.5f, -28.5f, -36f);
            for (var i = 1; i <= 4; i++)
                fight.Tick(t + i);

            Assert.AreEqual(2, fight.Phase, "advanced exactly once");
            Assert.AreEqual(1, Live(world, HazardMechanicRegistry.Wisps));
        }

        // ================================================================ round 2 A7, A5, B1 through the real core

        /// <summary>
        /// What the RUNTIME receives (FakePinnacleWorld.Spawn's healthMultiplier, the value LivePinnacleWorld passes to ApplyTier):
        /// 2.0 for the head and the crystal, 1.0 for the guardians and the summons, and 2.0 for every crystal across the four phases.
        /// </summary>
        [TestMethod]
        public void HealthMultiplier_TheWorldSpawns_HeadAt20_CrystalsAt2_GuardiansAndSummonsAt1()
        {
            var (fight, world, script, head) = Begun();

            Assert.AreEqual(20.0, world.Spawns.Single(s => s.Wcid == HarbingerData.HeadWcid).HealthMultiplier, "the head (round 4: 10x the old 2.0)");

            ToAlcove(fight, world, script, head);
            fight.Tick(HarbingerSettings.Defaults.SummonSeconds + 0.5);   // the summon timer fires (the Alcove stage still summons)

            Assert.AreEqual(2.0, world.Spawns.Single(s => s.Wcid == Acid.CrystalWcid).HealthMultiplier, "the crystal");
            Assert.AreEqual(2, world.Spawns.Count(s => s.Wcid == Acid.GuardianWcid));
            Assert.IsTrue(world.Spawns.Where(s => s.Wcid == Acid.GuardianWcid).All(s => s.HealthMultiplier == 1.0), "the guardians");
            Assert.IsTrue(world.Spawns.Any(s => s.Wcid == Acid.SummonWcid), "control: a summon spawned");
            Assert.IsTrue(world.Spawns.Where(s => s.Wcid == Acid.SummonWcid).All(s => s.HealthMultiplier == 1.0), "the summons");

            // every other element's crystal, guardians and summons, through the core's admin jump into each phase's threshold
            foreach (var def in HarbingerData.Elements.Skip(1))
            {
                var t = 100.0 * ((int)def.Element + 1);
                fight.JumpToPhase((int)def.Element + 1, t);
                var h = world.HandleOf(HarbingerData.HeadWcid);
                world.AtFloor.Add(h);
                fight.Tick(t + 1.1);
                world.AtFloor.Remove(h);
                fight.Tick(t + HarbingerSettings.Defaults.SummonSeconds + 0.5);

                Assert.AreEqual(2.0, world.Spawns.Single(s => s.Wcid == def.CrystalWcid).HealthMultiplier, def.Tag + " crystal");
                Assert.IsTrue(world.Spawns.Where(s => s.Wcid == def.GuardianWcid).All(s => s.HealthMultiplier == 1.0), def.Tag + " guardians");
                Assert.IsTrue(world.Spawns.Any(s => s.Wcid == def.SummonWcid), def.Tag + " control: a summon spawned");
                Assert.IsTrue(world.Spawns.Where(s => s.Wcid == def.SummonWcid).All(s => s.HealthMultiplier == 1.0), def.Tag + " summons");
            }

            // nothing else the script spawns carries a multiplier
            var doubled = new[] { HarbingerData.HeadWcid }.Concat(HarbingerData.Elements.Select(e => e.CrystalWcid)).ToHashSet();
            Assert.IsTrue(world.Spawns.Where(s => !doubled.Contains(s.Wcid)).All(s => s.HealthMultiplier == 1.0));
        }

        [TestMethod]
        public void Ward_ThroughTheCore_CrystalShieldedUntilBothGuardiansDie_ThenTheAttunement()
        {
            var (fight, world, script, head) = Begun();
            ToAlcove(fight, world, script, head);
            var crystal = world.HandleOf(Acid.CrystalWcid);
            var guards = world.Spawns.Where(s => s.Wcid == Acid.GuardianWcid).Select(s => s.Result).ToArray();

            Assert.IsTrue(world.Immune[crystal]);
            Assert.AreEqual(1.0, world.Floors[crystal]);

            world.Kill(guards[0]);
            fight.Tick(1.2);
            Assert.IsTrue(world.Immune[crystal], "one guardian still holds the ward");

            world.Kill(guards[1]);
            fight.Tick(1.25);
            Assert.IsFalse(world.Immune[crystal], "the ward is broken");
            Assert.IsNull(world.Floors[crystal]);
            Assert.AreEqual(0, world.OwnerBuffs.Count, "nothing until the crystal breaks");

            world.Kill(crystal);
            fight.Tick(1.3);
            Assert.AreEqual(HarbingerStage.Landing, script.Stage);
            Assert.AreEqual((head, 6080u, 120.0), world.OwnerBuffs.Single(), "the Legendary Acid Ward for 120 s, cast by the head");
        }

        [TestMethod]
        public void Death_InFinal_ResumesWithOneRunPerElement()
        {
            var (fight, world, script, head) = Begun();
            fight.JumpToPhase(5, 0.5);
            Assert.AreEqual(HarbingerStage.Final, script.Stage);

            foreach (var kind in new[] { HazardMechanicRegistry.Fill, HazardMechanicRegistry.Wisps, HazardMechanicRegistry.Rain, HazardMechanicRegistry.Balls })
                Assert.AreEqual(1, Live(world, kind), kind + " before");

            DieAndReturn(fight, world, 2.0);

            Assert.AreEqual(5, fight.Phase);
            Assert.AreEqual(HarbingerStage.Final, script.Stage);

            foreach (var kind in new[] { HazardMechanicRegistry.Fill, HazardMechanicRegistry.Wisps, HazardMechanicRegistry.Rain, HazardMechanicRegistry.Balls })
                Assert.AreEqual(1, Live(world, kind), kind + " after the resume");

            Assert.IsFalse(world.Immune[head]);
            Assert.AreEqual(0, world.Props.Count(p => HarbingerData.Alcoves.Any(a => a.PortalWcid == p.Prop.Wcid)), "no portal in the final");

            // the final's acid runs, so the four wall strips stand (round 2 A2), once each: the death and resume spawned none twice
            Assert.AreEqual(4 * HarbingerLayout.WallPanelRowZ.Count, LivePropsOf(world, ACE.Server.Pinnacles.Matron.MatronData.AcidFlowWcid), "every strip panel stands after the resume");
            Assert.AreEqual(4 * HarbingerLayout.WallPanelRowZ.Count, world.Props.Count(p => p.Prop.Wcid == ACE.Server.Pinnacles.Matron.MatronData.AcidFlowWcid), "and none was spawned twice");
        }
    }
}
