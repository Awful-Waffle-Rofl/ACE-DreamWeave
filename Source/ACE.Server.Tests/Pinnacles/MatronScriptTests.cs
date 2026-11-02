using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Matron;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The Matron encounter state machine (Docs/Knots/MATRON-ENCOUNTER-PLAN.md T7) against the recording fake port. The
    /// port applies nothing: a test drives the clock, the owner's position, the health floor and the fill level itself and
    /// delivers every event by hand. Reads no PropertyManager value except through PinnacleTunables.Matron, which is
    /// compared against PinnacleTunables.HazardPctClamp in the same process (whatever an earlier class seeded).
    /// </summary>
    [TestClass]
    public class MatronScriptTests
    {
        private const string Fill = MatronScript.TagFill;

        private static readonly MatronSettings S = MatronSettings.Defaults;

        // ================================================================ harness

        private static (MatronScript S, FakePinnacleFightPort P) Started(int seed = 1, MatronSettings settings = null)
        {
            var p = new FakePinnacleFightPort(seed);
            var s = new MatronScript(settings ?? MatronSettings.Defaults);
            s.OnStart(p);
            s.EnterPhase(1, p);
            return (s, p);
        }

        private static void Fire(MatronScript s, FakePinnacleFightPort p, string tag)
            => s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.TimerFired, Tag = tag }, p);

        /// <summary>Uses the newest LIVE prop spawned under <paramref name="tag"/>.</summary>
        private static void Use(MatronScript s, FakePinnacleFightPort p, string tag)
            => s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.ObjectUsed, Tag = tag, Handle = Live(p, tag) }, p);

        private static PinnacleHandle Live(FakePinnacleFightPort p, string tag)
            => AllProps(p).Last(x => x.Tag == tag && p.Alive.Contains(x.Result)).Result;

        /// <summary>Props recorded before a <see cref="Clear"/> stay findable by <see cref="Live"/>.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FakePinnacleFightPort, List<FakePinnacleFightPort.PropCall>> clearedProps = new();

        private static IEnumerable<FakePinnacleFightPort.PropCall> AllProps(FakePinnacleFightPort p)
            => (clearedProps.TryGetValue(p, out var old) ? old : Enumerable.Empty<FakePinnacleFightPort.PropCall>()).Concat(p.Props);

        /// <summary>ClearRecordings, keeping the prop history for <see cref="Live"/>.</summary>
        private static void Clear(FakePinnacleFightPort p)
        {
            clearedProps.GetOrCreateValue(p).AddRange(p.Props);
            p.ClearRecordings();
        }

        private static PinnacleHandle Door(MatronScript s, FakePinnacleFightPort p) => Live(p, MatronScript.DoorTag(s.PitOrder[0]));

        private static HazardArgs Args(FakePinnacleFightPort.HazardCall h) => h.Args;

        private static double StartZ(FakePinnacleFightPort.HazardCall h) => h.Args.GetDouble("start_z", double.NaN, -1000, 1000);

        private static double StartRate(FakePinnacleFightPort.HazardCall h) => h.Args.GetDouble("rate", double.NaN, -2, 2);

        private static double LastRate(FakePinnacleFightPort p) => p.Controls.Last(c => c.Tag == Fill && c.Key == "rate").Value;

        private static PinnaclePoint Floor(float x, float y) => new PinnaclePoint(MatronData.HallCell(x, y, -59.995f), x, y, -59.995f);

        private static PinnaclePoint Sill(MatronOpening o) => new PinnaclePoint(o.NicheCell, o.Centre.X + o.Outward.X, o.Centre.Y + o.Outward.Y, -48f);

        private static bool IsPlatform(FakePinnacleFightPort p, PinnacleHandle h) => p.Props.Any(x => x.Result == h && x.Prop.Wcid == MatronData.PlatformWcid);

        private static int LiveProps(FakePinnacleFightPort p) => p.Props.Count(x => p.Alive.Contains(x.Result));

        /// <summary>The fixtures a fight stands up: five doors, the black panel, the grate tiles and the iris (11).</summary>
        private static readonly int FixtureCount = 5 + 1 + MatronLayout.GrateTiles * MatronLayout.GrateTiles + 1;

        /// <summary>The grate tiles' tags, drain-grate-0 to drain-grate-3, in spawn order.</summary>
        private static IEnumerable<string> GrateTags() => Enumerable.Range(0, MatronLayout.GrateTiles * MatronLayout.GrateTiles).Select(i => $"{MatronScript.TagDrainGrate}-{i}");

        private static IEnumerable<FakePinnacleFightPort.HazardCall> FillStarts(FakePinnacleFightPort p) => p.Hazards.Where(h => h.Tag == Fill);

        /// <summary>The threshold fires: she runs to the next pit.</summary>
        private static void ToRunning(MatronScript s, FakePinnacleFightPort p)
        {
            p.AtFloor.Add(p.Head);
            Fire(s, p, MatronScript.TimerPoll);
            p.AtFloor.Remove(p.Head);
            Assert.AreEqual(MatronStep.Running, s.Step);
        }

        /// <summary>She arrives at the stand point and plays the open motion.</summary>
        private static void ToOpening(MatronScript s, FakePinnacleFightPort p)
        {
            ToRunning(s, p);
            p.PositionOf[p.Head] = MatronData.Opening(s.CurrentPit).Stand;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Opening, s.Step);
        }

        private static double FlowStart;

        /// <summary>The door opens and, the flow delay later, the acid starts: <see cref="FlowStart"/> is the delayed start.</summary>
        private static void ToFlowing(MatronScript s, FakePinnacleFightPort p)
        {
            ToDoorOpen(s, p);
            p.Now += s.Settings.FlowDelay;
            Fire(s, p, MatronScript.TimerFlowStart);
            FlowStart = p.Now;
            Assert.AreEqual(MatronStep.Flowing, s.Step);
            Assert.IsFalse(s.FlowWaiting);
        }

        /// <summary>The open timer: the door is open and the flow delay has begun (no acid yet).</summary>
        private static void ToDoorOpen(MatronScript s, FakePinnacleFightPort p)
        {
            ToOpening(s, p);
            Fire(s, p, MatronScript.TimerFace);
            Fire(s, p, MatronScript.TimerOpen);
            Assert.AreEqual(MatronStep.Flowing, s.Step);
        }

        /// <summary>The owner kills every wave add still alive.</summary>
        private static void KillWaves(MatronScript s, FakePinnacleFightPort p)
        {
            foreach (var h in s.WaveAdds)
                p.Kill(h);
        }

        private static void ToSealed(MatronScript s, FakePinnacleFightPort p)
        {
            ToFlowing(s, p);
            var pit = s.CurrentPit;
            p.OwnerAt = Sill(MatronData.Opening(pit));
            Use(s, p, MatronScript.DoorTag(pit));
            Assert.AreEqual(MatronStep.Sealed, s.Step);
        }

        /// <summary>Through <paramref name="flows"/> whole cycles; the owner kills each wave unless <paramref name="clearWaves"/> is false.</summary>
        private static void ToFighting(MatronScript s, FakePinnacleFightPort p, int flows, bool clearWaves = true)
        {
            for (var i = 0; i < flows; i++)
            {
                ToSealed(s, p);
                p.OwnerAt = Floor(220f, -150f);
                Fire(s, p, MatronScript.TimerPoll);
                Assert.AreEqual(MatronStep.Fighting, s.Step);

                if (clearWaves)
                    KillWaves(s, p);
            }

            Assert.AreEqual(flows, s.FlowsDone);
        }

        /// <summary>Finale: phase 2 entered after four flows, she has walked to the alcove, turned and played the opening motion.</summary>
        private static (MatronScript S, FakePinnacleFightPort P) InFinaleOpening()
        {
            var (s, p) = Started();
            ToFighting(s, p, 4);
            p.AtFloor.Add(p.Head);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(1, p.AdvanceCount);
            p.Phase = 2;
            s.EnterPhase(2, p);
            Assert.AreEqual(MatronStep.FinaleWalking, s.Step);
            p.PositionOf[p.Head] = MatronData.FinalePoint;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.FinaleOpening, s.Step);
            Fire(s, p, MatronScript.TimerFace);
            return (s, p);
        }

        /// <summary>The finale rock is broken (its AddDied).</summary>
        private static void BreakRock(MatronScript s, FakePinnacleFightPort p)
        {
            var rock = s.Rock;
            p.Kill(rock);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = MatronScript.TagRock, Handle = rock }, p);
        }

        private static void HeadDied(MatronScript s, FakePinnacleFightPort p)
            => s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = PinnacleEvent.HeadTag, Handle = p.Head }, p);

        private static bool EverFallen(FakePinnacleFightPort p) => p.Motions.Any(m => m.Motion == MotionCommand.Fallen);

        // ================================================================ the plan's T7 block

        [TestMethod]
        public void Start_AttackableEngagedFloorAt80()
        {
            var (_, p) = Started();
            Assert.IsTrue(p.Attackable[p.Head]);
            CollectionAssert.Contains(p.Engaged, p.Head);
            Assert.AreEqual(0.8, p.Floors[p.Head]);
        }

        [TestMethod]
        public void PitOrder_IsAPermutationOfTheFourPits()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                var (s, _) = Started(seed);
                CollectionAssert.AreEquivalent(new[] { 1, 2, 4, 5 }, s.PitOrder.ToArray(), $"seed {seed}");
            }
        }

        [TestMethod]
        public void Threshold_AtFloor_ImmunePeacefulRunsToItsPit()
        {
            var (s, p) = Started();
            p.AtFloor.Add(p.Head);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.IsTrue(p.Immune[p.Head]);
            Assert.IsTrue(p.Peaceful[p.Head]);
            Assert.AreEqual(MatronData.Opening(s.PitOrder[0]).Stand, p.MoveTos.Last().To);
            Assert.AreEqual(MatronStep.Running, s.Step);
        }

        [TestMethod]
        public void Flow_SpawnsDoorWallStripFourPlatforms_NoGeysers_StartsFillAt003()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            Assert.AreEqual(4, p.Props.Count(x => x.Prop.Wcid == MatronData.PlatformWcid));
            var strip = p.Props.Where(x => x.Tag.StartsWith($"strip-{s.CurrentPit}-", StringComparison.Ordinal)).ToList();
            CollectionAssert.AreEqual(MatronLayout.WallStrip(MatronData.Opening(s.CurrentPit), lava: false, MatronLayout.WallFxBoth).ToArray(), strip.Select(x => x.Prop).ToArray(), "the acid wall strip, as the layout builds it");
            Assert.AreEqual(6, strip.Count, "four flow panels and two fog hosts (default mode both)");
            Assert.AreEqual(4, strip.Count(x => x.Prop.Wcid == MatronData.AcidFlowWcid));
            Assert.AreEqual(2, strip.Count(x => x.Prop.Wcid == MatronData.EffectHostWcid && x.Prop.Script == MatronLayout.WallFogAcidScript));
            Assert.AreEqual(0, p.Props.Count(x => x.Tag.StartsWith("curtain", StringComparison.Ordinal) || x.Tag.StartsWith("glow", StringComparison.Ordinal)), "no curtain or glow stack any more");
            Assert.AreEqual(0, p.Props.Count(x => x.Prop.Wcid == 20635u || x.Tag.StartsWith("geyser", StringComparison.Ordinal)), "no geysers: the fog replaced the upward spray (owner, playtest 4)");
            Assert.AreEqual(1, p.Props.Count(x => x.Tag.StartsWith("panel", StringComparison.Ordinal)));
            Assert.IsTrue(p.DoorOpen[Door(s, p)]);
            Assert.AreEqual(0.03, Args(p.Hazards.Last()).GetDouble("rate", 0, -2, 2), 1e-9);
            Assert.AreEqual(HazardMechanicRegistry.Fill, p.Hazards.Last().Kind);
            CollectionAssert.Contains(p.Engaged, p.Head);
        }

        [TestMethod]
        public void WallFx_PicksTheStrip_PanelsOnly_FogOnly_Both_InEveryFlowAndTheLavaExit()
        {
            foreach (var (mode, panels, fog) in new[] { (MatronLayout.WallFxPanels, 4, 0), (MatronLayout.WallFxFog, 0, 2), (MatronLayout.WallFxBoth, 4, 2) })
            {
                var (s, p) = Started(settings: MatronSettings.Defaults with { WallFx = mode });
                ToFlowing(s, p);

                var strip = p.Props.Where(x => x.Tag.StartsWith($"strip-{s.CurrentPit}-", StringComparison.Ordinal)).Select(x => x.Prop).ToList();
                CollectionAssert.AreEqual(MatronLayout.WallStrip(MatronData.Opening(s.CurrentPit), lava: false, mode).ToArray(), strip.ToArray(), $"mode {mode}");
                Assert.AreEqual(panels, strip.Count(x => x.Wcid == MatronData.AcidFlowWcid), $"mode {mode}: flow panels");
                Assert.AreEqual(fog, strip.Count(x => x.Wcid == MatronData.EffectHostWcid), $"mode {mode}: fog host");
            }

            // and the per-copy key reaches it
            Assert.IsTrue(MatronOverrides.TryCreateScript(MatronSettings.Defaults, HazardArgs.Parse("wall_fx=1"), out var script, out var error), error);
            Assert.AreEqual(MatronLayout.WallFxFog, script.Settings.WallFx);
            Assert.AreEqual(MatronLayout.WallFxBoth, MatronSettings.Defaults.WallFx, "both by default");
        }

        [TestMethod]
        public void DoorUse_Seals_RateZero_StaysImmune()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            Use(s, p, MatronScript.DoorTag(s.PitOrder[0]));
            Assert.IsFalse(p.DoorOpen[Door(s, p)]);
            Assert.AreEqual((Fill, "rate", 0.0), p.Controls.Last());
            Assert.IsTrue(p.Immune[p.Head]);
            Assert.AreEqual(0, p.Props.Count(x => x.Tag.StartsWith("strip-", StringComparison.Ordinal) && p.Alive.Contains(x.Result)), "wall strip gone");

            Assert.AreEqual(4, p.Props.Count(x => IsPlatform(p, x.Result) && p.Alive.Contains(x.Result)), "platforms stay until landing");
        }

        [TestMethod]
        public void Perch_OnLowestPlatform_IsNotLanding()
        {
            var (s, p) = Started();
            ToSealed(s, p);
            p.OwnerAt = new PinnaclePoint(0x586B0103, 200f, -133f, -57.333f);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Sealed, s.Step);
            Assert.AreEqual(0, p.Spawns.Count);
            Assert.IsTrue(p.Immune[p.Head]);
        }

        [TestMethod]
        public void Landing_ClearsPlatformsSpawnsWaveLiftsImmunityLowersFloor()
        {
            var (s, p) = Started();
            ToSealed(s, p);
            p.OwnerAt = Floor(210, -125);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(6, p.Spawns.Count);
            Assert.IsTrue(p.Spawns.All(x => x.Tier == MatronData.AddTier));
            Assert.IsFalse(p.Immune[p.Head]);
            Assert.AreEqual(0.6, p.Floors[p.Head]);
            Assert.AreEqual(4, p.DestroyedProps.Count(h => IsPlatform(p, h)));
            Assert.AreEqual(MatronStep.Fighting, s.Step);
        }

        [TestMethod]
        public void FourthLanding_FloorIsOneHp_ThenAdvances()
        {
            var (s, p) = Started();
            ToFighting(s, p, flows: 4);
            Assert.AreEqual(0.0, p.Floors[p.Head]);
            p.AtFloor.Add(p.Head);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(1, p.AdvanceCount);

            // a second poll before the core applies the advance does not ask again
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(1, p.AdvanceCount);
        }

        [TestMethod]
        public void Unsealed_RateDoublesEvery45s_CappedAt024()
        {
            var (s, p) = Started();
            ToFlowing(s, p);

            foreach (var (t, r) in new[] { (44.0, 0.03), (45.0, 0.06), (90.0, 0.12), (135.0, 0.24), (180.0, 0.24), (900.0, 0.24) })
            {
                p.Now = FlowStart + t;
                Fire(s, p, MatronScript.TimerPoll);
                Assert.AreEqual(r, t < 45 ? StartRate(p.Hazards.Last()) : LastRate(p), 1e-9, $"t={t}");
            }
        }

        [TestMethod]
        public void Seal_WhileDraining_KeepsDraining()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            p.HazardLevel[Fill] = -58.0;
            Use(s, p, MatronScript.TagDrain);
            Use(s, p, MatronScript.DoorTag(s.PitOrder[0]));
            Assert.AreEqual(-1.0, LastRate(p));
            Assert.AreEqual(MatronStep.Sealed, s.Step);
        }

        [TestMethod]
        public void Drain_DuringFlow_ResumesFlowRateAtFloor()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            p.Now = FlowStart + 50;   // escalated once: 0.06
            Fire(s, p, MatronScript.TimerPoll);
            p.HazardLevel[Fill] = -58.0;
            Use(s, p, MatronScript.TagDrain);
            Assert.AreEqual(-1.0, LastRate(p));
            Assert.AreEqual(1.95 / 1.0 + 1.0, p.TimerSets.Last(t => t.Tag == MatronScript.TimerDrainDone).Seconds, 1e-9);
            p.HazardLevel[Fill] = S.FillFloorZ;
            Fire(s, p, MatronScript.TimerDrainDone);
            Assert.AreEqual(0.06, LastRate(p), 1e-9, "the escalated flow rate, not a fresh 0.03");
            Assert.IsFalse(s.Draining);
        }

        [TestMethod]
        public void Drain_OncePerFight_AndRefusedUnderQuarterMetre()
        {
            var (s, p) = Started();
            ToFlowing(s, p);

            p.HazardLevel[Fill] = S.FillFloorZ + 0.2;
            Use(s, p, MatronScript.TagDrain);
            Assert.IsFalse(s.DrainSpent);
            Assert.AreEqual(MatronLines.LineDrainEmpty, p.Said.Last());
            Assert.IsFalse(p.Controls.Any(c => c.Value < 0));

            p.HazardLevel[Fill] = S.FillFloorZ + 2.0;
            Use(s, p, MatronScript.TagDrain);
            Assert.IsTrue(s.DrainSpent);
            Assert.IsTrue(p.DoorOpen[Live(p, MatronScript.TagDrain)]);
            var controls = p.Controls.Count;

            Use(s, p, MatronScript.TagDrain);
            Assert.AreEqual(controls, p.Controls.Count, "a second use sends nothing");
            Assert.AreEqual(MatronLines.LineDrainSpent, p.Said.Last());
        }

        [TestMethod]
        public void Restart_AfterDrain_DrainStaysSpent()
        {
            var (s, p) = Started();
            // leftover acid at the threshold: the cycle's checkpoint is above the floor, so only the drain brings it down
            p.HazardLevel[Fill] = -59.5;
            ToFlowing(s, p);
            p.HazardLevel[Fill] = -57.0;
            Use(s, p, MatronScript.TagDrain);
            s.RestartPhase(p);
            Assert.IsTrue(s.DrainSpent);
            Assert.IsFalse(s.Draining, "a restart ends a drain in progress: it already re-checkpointed to the floor");
            Assert.AreEqual(S.FillFloorZ, StartZ(p.Hazards.Last()), 1e-9);
            Assert.AreEqual(0.03, StartRate(p.Hazards.Last()), 1e-9);

            // the drain prop is still open and still refuses
            Use(s, p, MatronScript.TagDrain);
            Assert.AreEqual(MatronLines.LineDrainSpent, p.Said.Last());

            // and the PROP state says the same: a restart leaves the props untouched, so the iris is the one live open drain prop
            Assert.IsTrue(p.DoorOpen[Live(p, MatronScript.TagDrain)], "the iris is still open after the restart");
            Assert.IsTrue(GrateTags().All(t => p.Alive.Contains(Live(p, t))) && p.Alive.Contains(Live(p, MatronScript.TagDrainFloor)), "the grate tiles and the black panel are still live");
        }

        [TestMethod]
        public void EnterLoopAgain_AfterTheDrainIsSpent_RespawnsTheIrisOpen_AndDestroysTheOldLayers()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            p.HazardLevel[Fill] = S.FillFloorZ + 2.0;
            Use(s, p, MatronScript.TagDrain);
            Assert.IsTrue(s.DrainSpent);

            var oldIris = Live(p, MatronScript.TagDrain);
            var oldGrates = GrateTags().Select(t => Live(p, t)).ToList();
            var oldFloor = Live(p, MatronScript.TagDrainFloor);
            Assert.IsTrue(p.DoorOpen[oldIris], "precondition: the used iris is open");

            // EnterPhase(1) runs EnterLoop: DestroyAllProps, then SpawnFixtures (the new iris is spawned with DrainSpent)
            s.EnterPhase(1, p);

            var iris = Live(p, MatronScript.TagDrain);
            var spawned = AllProps(p).Last(x => x.Result.Equals(iris));
            Assert.AreNotEqual(oldIris, iris, "a new iris was spawned");
            Assert.AreEqual(true, spawned.Prop.DoorOpen, "the new iris is spawned OPEN: the drain stays spent");
            Assert.IsTrue(p.DoorOpen[iris]);
            Assert.AreEqual(MatronLayout.DrainIris(true), spawned.Prop);

            Assert.AreEqual(FixtureCount, LiveProps(p), "five doors and the drain's black panel, four grate tiles and iris, no duplicates");
            foreach (var old in oldGrates.Append(oldIris).Append(oldFloor))
            {
                Assert.IsFalse(p.Alive.Contains(old), "an old drain layer was destroyed");
                CollectionAssert.Contains(p.DestroyedProps, old);
            }

            foreach (var tag in GrateTags())
                Assert.AreEqual(1, AllProps(p).Count(x => x.Tag == tag && p.Alive.Contains(x.Result)), $"one live {tag}, no orphan");
            Assert.AreEqual(1, AllProps(p).Count(x => x.Tag == MatronScript.TagDrainFloor && p.Alive.Contains(x.Result)), "one live black panel, no orphan");
            Assert.AreEqual(1, AllProps(p).Count(x => x.Tag == MatronScript.TagDrain && p.Alive.Contains(x.Result)), "one live iris, no orphan");
        }

        [TestMethod]
        public void Restart_DuringFlow_RestartsAtCheckpointWithFlowRate_PropsUntouched()
        {
            var (s, p) = Started();
            p.HazardLevel[Fill] = -59.5;   // the level when the threshold fires (acid left from an earlier cycle)
            ToFlowing(s, p);
            Assert.AreEqual(-59.5, s.Checkpoint, 1e-9);

            p.Now = FlowStart + 100;
            Fire(s, p, MatronScript.TimerPoll);
            p.HazardLevel[Fill] = -55.0;
            Fire(s, p, MatronScript.TimerPoll);

            Clear(p);
            s.RestartPhase(p);

            Assert.AreEqual(0, p.DestroyedProps.Count);
            Assert.AreEqual(0, p.Props.Count);
            Assert.AreEqual(1, FillStarts(p).Count());
            Assert.AreEqual(-59.5, StartZ(p.Hazards.Last()), 1e-9);
            Assert.AreEqual(0.03, StartRate(p.Hazards.Last()), 1e-9, "the reopened flow starts its escalation again");
            Assert.AreEqual(MatronStep.Flowing, s.Step);
            CollectionAssert.Contains(p.Engaged, p.Head);
            Assert.IsTrue(p.DoorOpen[Door(s, p)], "the flow is still open");
        }

        [TestMethod]
        public void Restart_DeepCheckpoint_IsCappedAtReturnMaxDepth_AndStaysCapped()
        {
            // prod 2026-10-30: the returning player lands on the floor; acid over their head killed them on arrival
            var (s, p) = Started();
            p.HazardLevel[Fill] = -48.0;   // the acid left from earlier flows when the threshold fires: 12 m deep
            ToFlowing(s, p);
            Assert.AreEqual(-48.0, s.Checkpoint, 1e-9);

            Clear(p);
            s.RestartPhase(p);
            var capped = S.FillFloorZ + S.ReturnMaxDepth;
            Assert.AreEqual(capped, StartZ(p.Hazards.Last()), 1e-9);
            Assert.AreEqual(capped, s.Checkpoint, 1e-9, "the capped level is the new checkpoint");

            // the acid rises again, then a second death: it restarts at the capped checkpoint, not the old 12 m
            p.HazardLevel[Fill] = -57.5;
            Fire(s, p, MatronScript.TimerPoll);
            p.HazardLevel.Remove(Fill);
            Clear(p);
            s.RestartPhase(p);
            Assert.AreEqual(capped, StartZ(p.Hazards.Last()), 1e-9);
        }

        [TestMethod]
        public void Restart_ReturnMaxDepth_Override_SetsTheCap()
        {
            Assert.AreEqual(1.0, S.ReturnMaxDepth);
            Assert.IsTrue(MatronOverrides.TryApply(S, HazardArgs.Parse("return_max_depth=0"), out var zero, out var error), error);
            Assert.IsFalse(MatronOverrides.TryApply(S, HazardArgs.Parse("return_max_depth=-1"), out _, out _));
            Assert.IsFalse(MatronOverrides.TryApply(S, HazardArgs.Parse("return_max_depth=20.5"), out _, out _));

            var (s, p) = Started(settings: zero);
            p.HazardLevel[Fill] = -55.0;
            ToFlowing(s, p);
            Clear(p);
            s.RestartPhase(p);
            Assert.AreEqual(S.FillFloorZ, StartZ(p.Hazards.Last()), 1e-9, "a 0 m cap restarts the acid at the floor");
        }

        [TestMethod]
        public void RestartPhase_WhileRunningToPit_ReassertsPeacefulAndMove()
        {
            var (s, p) = Started();
            ToRunning(s, p);
            p.Peaceful[p.Head] = false;   // the core's RestorePacified at the resume
            Clear(p);
            s.RestartPhase(p);
            Assert.IsTrue(p.Peaceful[p.Head]);
            Assert.AreEqual(MatronData.Opening(s.PitOrder[0]).Stand, p.MoveTos.Last().To);
            Assert.AreEqual(0, p.Engaged.Count, "Engage would cancel the walk");
            Assert.IsTrue(p.Immune[p.Head]);
        }

        [TestMethod]
        public void RestartPhase_FinaleOpening_ReassertsPeaceful_TurnsAndPlaysTheOpeningAgain_NeverFallen()
        {
            var (s, p) = InFinaleOpening();
            p.Peaceful[p.Head] = false;
            Clear(p);
            s.RestartPhase(p);
            Assert.IsTrue(p.Peaceful[p.Head]);
            Assert.IsTrue(p.Immune[p.Head]);
            Assert.AreEqual(MatronData.FinalePoint, p.Turns.Single().Facing);
            CollectionAssert.Contains(p.CancelledTimers, MatronScript.TimerRumble, "a pending rumble would cut the motion short");
            Fire(s, p, MatronScript.TimerFace);
            Assert.AreEqual(new FakePinnacleFightPort.MotionCall(p.Head, MotionStance.HandCombat, MotionCommand.SpecialAttack1, false), p.Motions.Last());
            Assert.AreEqual(MatronScript.RumbleSeconds, p.TimerSets.Last(t => t.Tag == MatronScript.TimerRumble).Seconds, 1e-9);
            Assert.AreEqual(0, p.Engaged.Count);
            Assert.IsFalse(EverFallen(p));
        }

        [TestMethod]
        public void Finale_RockDeath_LetsTheLavaOut_ClosesThePits_RateMinus2()
        {
            var (s, p) = InFinaleOpening();
            Fire(s, p, MatronScript.TimerRumble);
            Assert.AreEqual(MatronStep.FinaleRace, s.Step);
            Clear(p);
            p.ControllableTags = new HashSet<string> { Fill };   // ClearRecordings forgot the started run

            BreakRock(s, p);
            Assert.AreEqual(MatronStep.FinaleLava, s.Step);
            Assert.AreEqual(MatronLines.LineLavaDoor, p.Said.Single());
            Assert.AreEqual(1, p.Props.Count(x => x.Tag == "lava-panel" && x.Prop.Wcid == MatronData.LavaPanelWcid));
            var lavaStrip = p.Props.Where(x => x.Tag.StartsWith("lava-strip-", StringComparison.Ordinal)).Select(x => x.Prop).ToArray();
            CollectionAssert.AreEqual(MatronLayout.WallStrip(MatronData.Opening(MatronData.NorthId), lava: true, MatronLayout.WallFxBoth).ToArray(), lavaStrip, "the lava wall strip in the north opening");
            Assert.AreEqual(4, lavaStrip.Count(x => x.Wcid == MatronData.LavaFlowWcid && x.Translucency == null), "four lava flow panels, no translucency");
            Assert.AreEqual(2, lavaStrip.Count(x => x.Wcid == MatronData.EffectHostWcid && x.Script == MatronLayout.WallFogLavaScript), "the orange fog, two hosts");
            Assert.AreEqual(3, p.Props.Count(x => x.Tag.StartsWith("lava-pool", StringComparison.Ordinal)));
            Assert.AreEqual(0, p.Props.Count(x => x.Prop.Wcid == MatronData.EffectHostWcid && x.Prop.Script == MatronData.PillarScript), "no pillar before the slay");

            // the north niche's lava floor: the acid niche floor's placement, lava panels at the lava floor's translucency
            var north = MatronData.Opening(MatronData.NorthId);
            var lavaFloor = p.Props.Where(x => x.Tag.StartsWith("lava-floor-", StringComparison.Ordinal)).ToList();
            var acidFloor = MatronLayout.NicheFloor(north);
            Assert.AreEqual(2, lavaFloor.Count);
            for (var i = 0; i < lavaFloor.Count; i++)
            {
                Assert.AreEqual(MatronData.LavaPanelWcid, lavaFloor[i].Prop.Wcid);
                Assert.AreEqual(1006968u, lavaFloor[i].Prop.Wcid);
                Assert.AreEqual(acidFloor[i].At, lavaFloor[i].Prop.At, "same point as an acid niche floor would take");
                Assert.AreEqual(acidFloor[i].Scale, lavaFloor[i].Prop.Scale);
                Assert.AreEqual(HazardPanels.TranslucencyFor(MatronScript.LavaFloorOpacity).Value, lavaFloor[i].Prop.Translucency.Value, 1e-6f);
                Assert.AreEqual(north.NicheCell, lavaFloor[i].Prop.At.Cell);
                Assert.AreEqual(-48f + MatronLayout.NicheFloorLift, lavaFloor[i].Prop.At.Z, 1e-4f);
            }
            Assert.AreEqual(-2.0, LastRate(p));
            foreach (var pit in MatronData.PitIds)
                Assert.IsFalse(p.DoorOpen[Live(p, MatronScript.DoorTag(pit))], $"pit {pit} closed");
            Assert.AreEqual(0, AllProps(p).Count(x => (x.Tag.StartsWith("strip-", StringComparison.Ordinal) || x.Tag.StartsWith("niche-floor", StringComparison.Ordinal)) && p.Alive.Contains(x.Result)), "the acid flows are gone");
            Assert.AreEqual(MatronScript.PillarSeconds, p.TimerSets.Single(t => t.Tag == MatronScript.TimerPillar).Seconds, 1e-9);
            Assert.IsFalse(p.TimerSets.Any(t => t.Tag == MatronScript.TimerSlay), "the slay waits for the pillar");
            Assert.AreEqual(0, p.Slain.Count);
        }

        [TestMethod]
        public void Finale_PillarOnHerAtTheFixedDelay_ThenTheSlayInsideIt_ThenTheWin_PillarSurvives()
        {
            var (s, p) = InFinaleOpening();
            Fire(s, p, MatronScript.TimerRumble);
            p.HazardLevel[Fill] = -46.0;   // acid still high: the slay does not wait for it
            Fire(s, p, MatronScript.TimerPoll);
            BreakRock(s, p);

            var body = new PinnaclePoint(0x586B0108, 212.4f, -111.1f, MatronData.FloorZ);
            p.PositionOf[p.Head] = body;

            // a slay timer before the pillar (stale) does not kill her
            Fire(s, p, MatronScript.TimerSlay);
            Assert.AreEqual(0, p.Slain.Count, "never slain before the pillar");

            p.Now += MatronScript.PillarSeconds;
            Fire(s, p, MatronScript.TimerPillar);
            var pillars = p.Props.Where(x => x.Prop.Wcid == MatronData.EffectHostWcid && x.Prop.Script == MatronData.PillarScript).ToList();
            Assert.AreEqual(1, pillars.Count, "one pillar host: three read as a solid wall");
            Assert.AreEqual((body.Cell, body.X, body.Y, MatronData.FloorZ), (pillars[0].Prop.At.Cell, pillars[0].Prop.At.X, pillars[0].Prop.At.Y, pillars[0].Prop.At.Z), "on her body");
            Assert.AreEqual(0, p.Slain.Count, "the fire hits her before she dies");
            Assert.AreEqual(MatronScript.SlaySeconds, p.TimerSets.Last(t => t.Tag == MatronScript.TimerSlay).Seconds, 1e-9);

            p.Now += MatronScript.SlaySeconds;
            Fire(s, p, MatronScript.TimerSlay);
            CollectionAssert.AreEqual(new[] { p.Head }, p.Slain);
            Assert.AreEqual(0, p.WinCount, "the win waits for her death event");

            HeadDied(s, p);
            Assert.AreEqual(1, p.WinCount);
            Assert.AreEqual(MatronLines.LineWin, p.Said.Last());
            Assert.AreEqual(0, pillars.Count(x => p.DestroyedProps.Contains(x.Result)), "the pillar outlives her: End cleans props");

            // stale timers after the win change nothing
            Fire(s, p, MatronScript.TimerSlay);
            Fire(s, p, MatronScript.TimerPillar);
            Assert.AreEqual(1, p.Slain.Count);
            Assert.AreEqual(1, p.Props.Count(x => x.Prop.Wcid == MatronData.EffectHostWcid && x.Prop.Script == MatronData.PillarScript));
        }

        [TestMethod]
        public void Finale_DeathReportedAfterTheSlay_WinsAtOnce()
        {
            var (s, p) = InLava(S.FillFloorZ);
            p.Now += MatronScript.PillarSeconds;
            Fire(s, p, MatronScript.TimerPillar);
            p.Now += MatronScript.SlaySeconds;
            Fire(s, p, MatronScript.TimerSlay);
            Assert.AreEqual(0, p.WinCount, "she has not been reported dead yet");
            HeadDied(s, p);
            Assert.AreEqual(1, p.WinCount);
            Assert.AreEqual(1, p.Props.Count(x => x.Prop.Wcid == MatronData.EffectHostWcid && x.Prop.Script == MatronData.PillarScript));
        }

        [TestMethod]
        public void Matron_HeadLeavesACorpse_GraelDoesNot()
        {
            Assert.IsTrue(MatronData.Script().HeadLeavesCorpse);
            Assert.IsFalse(ACE.Server.Pinnacles.Grael.GraelData.Script().HeadLeavesCorpse);
        }

        // ================================================================ beyond the plan

        [TestMethod]
        public void Doors_SpawnClosed_AtDoorScale_DrainIrisOverGrateOverBlack()
        {
            var (_, p) = Started();

            var doors = p.Props.Where(x => x.Prop.Wcid == MatronData.DoorWcid && x.Tag != MatronScript.TagDrain).ToList();
            Assert.AreEqual(5, doors.Count);
            Assert.IsTrue(doors.All(d => d.Prop.Scale == MatronLayout.DoorScale && d.Prop.DoorOpen == false));
            // the scaled leaves (1.47 m behind the origin at scale 1) end exactly on the niche's back wall
            var nw = MatronData.Opening(1);
            Assert.AreEqual(0.8f, MatronLayout.DoorScale);
            Assert.AreEqual(3.0f, MatronLayout.DoorHeight * MatronLayout.DoorScale, 1e-4f, "the door fills the 3 m opening");
            Assert.AreEqual(3.33f - 1.47f * 0.8f, MatronLayout.Door(nw).Y - nw.Centre.Y, 1e-4f);

            // the drain: the iris is the one TagDrain prop (the use target); the grate and the black panel have their own tags
            var drains = p.Props.Where(x => x.Tag == MatronScript.TagDrain).ToList();
            Assert.AreEqual(1, drains.Count, "exactly one use target");
            Assert.AreEqual(MatronLayout.DrainIris(false), drains[0].Prop);
            Assert.AreEqual(MatronData.DrainIrisWcid, drains[0].Prop.Wcid);
            Assert.AreEqual(false, drains[0].Prop.DoorOpen);

            // the grate is four tiles, one tag each (drain-grate-0 to -3, the layout's order), every tile its layout prop
            var grates = p.Props.Where(x => x.Tag.StartsWith(MatronScript.TagDrainGrate, StringComparison.Ordinal)).ToList();
            var floors = p.Props.Where(x => x.Tag == MatronScript.TagDrainFloor).ToList();
            CollectionAssert.AreEqual(GrateTags().ToList(), grates.Select(x => x.Tag).ToList(), "four tiles, unique tags, in layout order");
            CollectionAssert.AreEqual(MatronLayout.DrainGrate().ToList(), grates.Select(x => x.Prop).ToList());
            Assert.AreEqual(1, floors.Count);
            Assert.AreEqual(MatronLayout.DrainFloor(), floors[0].Prop);
            Assert.AreEqual("drain-grate", MatronScript.TagDrainGrate);
            Assert.AreEqual("drain-floor", MatronScript.TagDrainFloor);
            Assert.IsFalse(grates.Any(x => x.Tag == MatronScript.TagDrain), "no grate tile carries the use target's tag");

            // spawn order, bottom up: black, every grate tile, iris
            var order = p.Props.Select(x => x.Tag).ToList();
            var tileIdx = GrateTags().Select(t => order.IndexOf(t)).ToList();
            Assert.IsTrue(order.IndexOf(MatronScript.TagDrainFloor) < tileIdx.Min() && tileIdx.Max() < order.IndexOf(MatronScript.TagDrain));

            // the Olthoi door wcid is spent on the five openings only: none of the three drain layers is the plain door
            Assert.AreEqual(0, p.Props.Count(x => x.Prop.Wcid == MatronData.DoorWcid && x.Prop.At.Cell == MatronData.DrainPoint.Cell && x.Prop.At.X == MatronData.DrainPoint.X));
        }

        [TestMethod]
        public void Running_ArrivesByTimeout_WithNoPositionRead()
        {
            var (s, p) = Started();
            ToRunning(s, p);
            p.Now += MatronScript.MoveTimeout - 0.25;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Running, s.Step);
            p.Now += 0.25;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Opening, s.Step);
            Assert.AreEqual(4.0, MatronScript.MoveTimeout);
        }

        [TestMethod]
        public void Running_ArrivesWhereTheLiveWalkStops_NotOnlyOnTheStandPoint()
        {
            // the live walk ends when her 2.115 m sphere is 0.6 m from the point: her centre about 2.7 m short
            var (s, p) = Started();
            ToRunning(s, p);
            var stand = MatronData.Opening(s.CurrentPit).Stand;
            var o = MatronData.Opening(s.CurrentPit).Outward;

            p.PositionOf[p.Head] = stand with { X = stand.X - o.X * 3.2f, Y = stand.Y - o.Y * 3.2f };
            p.Now += MatronScript.WalkPollSeconds;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Running, s.Step, "3.2 m out: still walking");

            p.PositionOf[p.Head] = stand with { X = stand.X - o.X * 2.715f, Y = stand.Y - o.Y * 2.715f };
            p.Now += MatronScript.WalkPollSeconds;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Opening, s.Step, "where the walk stops: arrived");
            Assert.AreEqual(0.6 + 1.41 * 1.5 + 0.3, MatronScript.ArrivalRadius, 1e-9);
        }

        [TestMethod]
        public void Running_ArrivesWhenTheWalkStalls_AfterTheGrace()
        {
            var (s, p) = Started();
            ToRunning(s, p);
            var stand = MatronData.Opening(s.CurrentPit).Stand;
            p.PositionOf[p.Head] = stand with { X = stand.X + 8f };
            var t0 = p.Now;

            // no progress at all: inside the grace nothing is called
            for (var at = MatronScript.WalkPollSeconds; at < MatronScript.StallGraceSeconds - 1e-9; at += MatronScript.WalkPollSeconds)
            {
                p.Now = t0 + at;
                Fire(s, p, MatronScript.TimerPoll);
                Assert.AreEqual(MatronStep.Running, s.Step, $"t={at}");
            }

            // after it, StallChecks checks in a row without progress
            p.Now = t0 + MatronScript.StallGraceSeconds;
            for (var i = 1; i < MatronScript.StallChecks; i++)
            {
                Fire(s, p, MatronScript.TimerPoll);
                Assert.AreEqual(MatronStep.Running, s.Step, $"stall check {i}");
                p.Now += MatronScript.WalkPollSeconds;
            }

            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Opening, s.Step);
            Assert.IsTrue(p.Now - t0 < MatronScript.MoveTimeout, "the stall, not the timeout");
        }

        [TestMethod]
        public void Running_ProgressResetsTheStallCount()
        {
            var (s, p) = Started();
            ToRunning(s, p);
            var stand = MatronData.Opening(s.CurrentPit).Stand;
            var t0 = p.Now;
            var x = 7f;

            // closing 0.3 m a check (a real walk): never a stall, arrival by distance
            for (var i = 1; x > 3.1f; i++, x -= 0.3f)
            {
                p.PositionOf[p.Head] = stand with { X = stand.X + x };
                p.Now = t0 + i * MatronScript.WalkPollSeconds * 0.5;
                Fire(s, p, MatronScript.TimerPoll);
                Assert.AreEqual(MatronStep.Running, s.Step, $"x={x}");
            }

            p.PositionOf[p.Head] = stand with { X = stand.X + 2.9f };
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Opening, s.Step);
        }

        [TestMethod]
        public void Poll_RunsEveryQuarterSecond_WhileSheWalks()
        {
            var (s, p) = Started();
            ToRunning(s, p);
            Assert.AreEqual((MatronScript.TimerPoll, MatronScript.WalkPollSeconds), p.TimerSets.Last(t => t.Tag == MatronScript.TimerPoll));

            p.PositionOf[p.Head] = MatronData.Opening(s.CurrentPit).Stand;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual((MatronScript.TimerPoll, MatronScript.PollSeconds), p.TimerSets.Last(t => t.Tag == MatronScript.TimerPoll), "arrived: back to 1 s");
        }

        [TestMethod]
        public void Arrival_TurnsToTheStandHeading_ThenPlaysTheOpening_ThenOpens()
        {
            var (s, p) = Started();
            p.TurnSeconds = 0.8;
            ToRunning(s, p);
            Clear(p);
            var stand = MatronData.Opening(s.CurrentPit).Stand;
            p.PositionOf[p.Head] = stand;
            Fire(s, p, MatronScript.TimerPoll);

            Assert.AreEqual(MatronStep.Opening, s.Step);
            Assert.AreEqual(new FakePinnacleFightPort.TurnCall(p.Head, stand), p.Turns.Single());
            Assert.AreEqual(0.8 + MatronScript.TurnSettleSeconds, p.TimerSets.Single(t => t.Tag == MatronScript.TimerFace).Seconds, 1e-9);
            Assert.AreEqual(0, p.Motions.Count, "no motion before the turn is done");
            Assert.IsFalse(p.TimerSets.Any(t => t.Tag == MatronScript.TimerOpen));

            Fire(s, p, MatronScript.TimerFace);
            Assert.AreEqual(new FakePinnacleFightPort.MotionCall(p.Head, MotionStance.HandCombat, MotionCommand.SpecialAttack1, false), p.Motions.Single());
            Assert.AreEqual(PlayScript.BreatheAcid, p.Effects.Single().Script);
            Assert.AreEqual(MatronScript.OpenSeconds, p.TimerSets.Single(t => t.Tag == MatronScript.TimerOpen).Seconds, 1e-9);

            // the stand point's rotation faces the opening (yaw toward Outward)
            var q = new System.Numerics.Quaternion(stand.QX, stand.QY, stand.QZ, stand.QW);
            var fwd = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, q);
            var o = MatronData.Opening(s.CurrentPit).Outward;
            Assert.AreEqual(o.X, fwd.X, 1e-4f);
            Assert.AreEqual(o.Y, fwd.Y, 1e-4f);
        }

        [TestMethod]
        public void RestartPhase_InOpening_CancelsThePendingOpen_TurnsAndPlaysAgain()
        {
            var (s, p) = Started();
            ToOpening(s, p);
            Fire(s, p, MatronScript.TimerFace);
            Assert.IsTrue(p.Timers.ContainsKey(MatronScript.TimerOpen));
            Clear(p);
            s.RestartPhase(p);
            CollectionAssert.Contains(p.CancelledTimers, MatronScript.TimerOpen);
            Assert.IsFalse(p.Timers.ContainsKey(MatronScript.TimerOpen));
            Assert.AreEqual(1, p.Turns.Count);
            Assert.IsTrue(p.Timers.ContainsKey(MatronScript.TimerFace));
            Assert.AreEqual(0, p.Engaged.Count);
        }

        [TestMethod]
        public void Flow_LaysADoubledFaceUpAcidFloorInTheNiche_GoneOnSeal()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            var o = MatronData.Opening(s.CurrentPit);
            var floor = p.Props.Where(x => x.Tag.StartsWith($"niche-floor-{o.Id}-", StringComparison.Ordinal)).ToList();
            Assert.AreEqual(2, floor.Count);
            Assert.AreEqual(0.05f, MatronLayout.NicheFloorLift, "0.02 m rests on the sill itself (dat-measured)");
            foreach (var f in floor)
            {
                Assert.AreEqual(MatronData.AcidPanelWcid, f.Prop.Wcid);
                Assert.AreEqual(0.3333f, f.Prop.Scale);
                Assert.AreEqual(o.NicheCell, f.Prop.At.Cell);
                Assert.AreEqual(-48f + MatronLayout.NicheFloorLift, f.Prop.At.Z, 1e-4f);
                Assert.AreEqual(o.Centre.X + o.Outward.X * 1.67f, f.Prop.At.X, 1e-4f);
                Assert.AreEqual(o.Centre.Y + o.Outward.Y * 1.67f, f.Prop.At.Y, 1e-4f);
                var up = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, new System.Numerics.Quaternion(f.Prop.At.QX, f.Prop.At.QY, f.Prop.At.QZ, f.Prop.At.QW));
                Assert.AreEqual(1f, up.Z, 1e-4f, "face up");
            }

            p.OwnerAt = Sill(o);
            Use(s, p, MatronScript.DoorTag(o.Id));
            Assert.IsTrue(floor.All(f => p.DestroyedProps.Contains(f.Result)));
        }

        [TestMethod]
        public void NeverEngages_WhileRunningOpeningOrFallen()
        {
            var (s, p) = Started();
            Clear(p);
            ToOpening(s, p);
            s.RestartPhase(p);
            Assert.AreEqual(0, p.Engaged.Count);

            var (s2, p2) = InFinaleOpening();
            Fire(s2, p2, MatronScript.TimerRumble);
            BreakRock(s2, p2);
            Clear(p2);
            s2.RestartPhase(p2);
            Fire(s2, p2, MatronScript.TimerPoll);
            Assert.AreEqual(0, p2.Engaged.Count);
            Assert.IsFalse(EverFallen(p2));
        }

        [TestMethod]
        public void RestartPhase_TwiceInARow_LeavesTheSameState_InEveryStep()
        {
            foreach (var step in Enum.GetValues(typeof(MatronStep)).Cast<MatronStep>())
            {
                var (s, p) = InStep(step);

                s.RestartPhase(p);
                var first = Snapshot(s, p);
                var startsBefore = FillStarts(p).Count();
                s.RestartPhase(p);
                var second = Snapshot(s, p);

                Assert.AreEqual(step, s.Step, $"{step}: a restart never moves the step");
                Assert.AreEqual(first, second, $"{step}");
                Assert.IsTrue(FillStarts(p).Count() - startsBefore <= 1, $"{step}: at most one fill start per restart");
                AssertNeverTwoFills(p, step.ToString());
            }
        }

        [TestMethod]
        public void StaleTimers_AreIgnored()
        {
            // open after the flow has opened
            var (s, p) = Started();
            ToFlowing(s, p);
            var props = p.Props.Count;
            var starts = p.Hazards.Count;
            Fire(s, p, MatronScript.TimerOpen);
            Fire(s, p, MatronScript.TimerFlowStart);   // and a second flow start after the acid has started
            Assert.AreEqual(props, p.Props.Count);
            Assert.AreEqual(starts, p.Hazards.Count);

            // open while still running
            var (s1, p1) = Started();
            ToRunning(s1, p1);
            Fire(s1, p1, MatronScript.TimerOpen);
            Assert.AreEqual(MatronStep.Running, s1.Step);

            // drain-done with no drain running
            Clear(p);
            Fire(s, p, MatronScript.TimerDrainDone);
            Assert.AreEqual(0, p.Controls.Count);

            // face, rumble, slay and pillar outside their steps
            Fire(s, p, MatronScript.TimerFace);
            Fire(s, p, MatronScript.TimerRumble);
            Fire(s, p, MatronScript.TimerSlay);
            Fire(s, p, MatronScript.TimerPillar);
            Assert.AreEqual(MatronStep.Flowing, s.Step);
            Assert.AreEqual(0, p.Slain.Count);
            Assert.AreEqual(0, p.Motions.Count + p.Props.Count);

            var (s2, p2) = InFinaleOpening();
            Fire(s2, p2, MatronScript.TimerRumble);
            Clear(p2);
            Fire(s2, p2, MatronScript.TimerRumble);
            Assert.AreEqual(0, p2.Props.Count, "a second rumble opens nothing");
            Fire(s2, p2, MatronScript.TimerSlay);
            Fire(s2, p2, MatronScript.TimerPillar);
            Assert.AreEqual(0, p2.Slain.Count);
            Assert.AreEqual(0, p2.Props.Count);

            // a rock death outside the race (a stale one) lets no lava out
            var (s3, p3) = InFinaleOpening();
            s3.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = MatronScript.TagRock, Handle = new PinnacleHandle(777) }, p3);
            Assert.AreEqual(MatronStep.FinaleOpening, s3.Step);
        }

        [TestMethod]
        public void ObjectUsed_WrongDoor_SealedDoor_NorthOutsideFinale_AreIgnored()
        {
            var (s, p) = Started();
            ToFighting(s, p, 1);
            var sealedPit = s.PitOrder[0];
            ToFlowing(s, p);
            Clear(p);

            Use(s, p, MatronScript.DoorTag(sealedPit));
            Use(s, p, MatronScript.DoorTag(s.PitOrder[2]));
            Use(s, p, MatronScript.DoorTag(MatronData.NorthId));
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.ObjectUsed, Tag = MatronScript.DoorTag(s.CurrentPit), Handle = new PinnacleHandle(9999) }, p);

            Assert.AreEqual(MatronStep.Flowing, s.Step);
            Assert.AreEqual(0, p.Controls.Count);
            Assert.AreEqual(0, p.CallsNamed(nameof(IPinnacleFightPort.SetDoorOpen)).Count(), "no door was touched");
            Assert.AreEqual(0, p.Said.Count);
        }

        [TestMethod]
        public void ObjectUsed_CurrentDoor_BeforeTheFlowOpens_IsIgnored()
        {
            var (s, p) = Started();
            ToOpening(s, p);
            Clear(p);
            Use(s, p, MatronScript.DoorTag(s.CurrentPit));
            Assert.AreEqual(MatronStep.Opening, s.Step);
            Assert.AreEqual(0, p.Calls.Count);
        }

        [TestMethod]
        public void EnterPhase1_Again_DestroysTrackedPropsBeforeSpawning_NoDuplicates()
        {
            var (s, p) = Started();
            var fixtures = LiveProps(p);
            Assert.AreEqual(FixtureCount, fixtures, "five doors and the drain's black panel, four grate tiles and iris");
            Assert.AreEqual(11, fixtures);

            ToFlowing(s, p);
            Assert.IsTrue(LiveProps(p) > fixtures);

            s.EnterPhase(1, p);
            Assert.AreEqual(fixtures, LiveProps(p));
            Assert.AreEqual(MatronStep.Fighting, s.Step);
            Assert.AreEqual(0, s.FlowsDone);
        }

        [TestMethod]
        public void EnterPhase2_Directly_StandsUpTheFixtures()
        {
            // an admin jump begins straight in phase 2: OnStart then EnterPhase(2), no phase 1
            var p = new FakePinnacleFightPort();
            var s = new MatronScript();
            s.OnStart(p);
            p.Phase = 2;
            s.EnterPhase(2, p);
            Assert.AreEqual(FixtureCount, LiveProps(p));
            Assert.AreEqual(MatronStep.FinaleWalking, s.Step);
            Assert.AreEqual(0.0, p.Floors[p.Head]);
            Assert.IsTrue(p.Immune[p.Head]);
            Assert.IsTrue(p.Peaceful[p.Head]);
            Assert.AreEqual(MatronData.FinalePoint, p.MoveTos.Last().To);
        }

        [TestMethod]
        public void Fill_NeverTwoRunsAtOnce_ThroughAWholeFight()
        {
            var (s, p) = Started();
            p.HazardLevel[Fill] = S.FillFloorZ;
            ToFighting(s, p, 2);
            p.HazardLevel[Fill] = -57.0;
            ToFlowing(s, p);
            Use(s, p, MatronScript.TagDrain);
            s.RestartPhase(p);
            Fire(s, p, MatronScript.TimerDrainDone);
            s.RestartPhase(p);
            AssertNeverTwoFills(p, "phase 1");

            var (s2, p2) = InFinaleOpening();
            Fire(s2, p2, MatronScript.TimerRumble);
            s2.RestartPhase(p2);
            BreakRock(s2, p2);
            s2.RestartPhase(p2);
            AssertNeverTwoFills(p2, "finale");
        }

        [TestMethod]
        public void HazardEnded_RestartsTheFill_AtMostFiveTimes()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            var starts = FillStarts(p).Count();

            for (var i = 0; i < 8; i++)
                s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.HazardEnded, Tag = Fill, Outcome = HazardOutcome.Aborted }, p);

            Assert.AreEqual(starts + 5, FillStarts(p).Count(), "the brief's cap: five restarts");
        }

        [TestMethod]
        public void HazardEnded_Stopped_OrWithTheRunStillLive_RestartsNothing()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            var starts = FillStarts(p).Count();

            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.HazardEnded, Tag = Fill, Outcome = HazardOutcome.Stopped }, p);
            Assert.AreEqual(starts, FillStarts(p).Count());

            // a stale end queued for a run already replaced: the port still reads a live run under the tag
            p.HazardLevel[Fill] = -59.0;
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.HazardEnded, Tag = Fill, Outcome = HazardOutcome.Aborted }, p);
            Assert.AreEqual(starts, FillStarts(p).Count());
        }

        [TestMethod]
        public void HazardEnded_ForTheFill_RestartsAtTheLastLevel()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            p.HazardLevel[Fill] = -56.0;
            Fire(s, p, MatronScript.TimerPoll);
            p.HazardLevel.Remove(Fill);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.HazardEnded, Tag = Fill, Outcome = HazardOutcome.Reaped }, p);
            Assert.AreEqual(-56.0, StartZ(p.Hazards.Last()), 1e-9);
            Assert.AreEqual(p.StoppedHazardTags.Count, p.StoppedHazardTags.Count(t => t == Fill));
        }

        [TestMethod]
        public void Restart_DuringSealed_KeepsThePitSealed_AndStaysImmuneUntilLanding()
        {
            var (s, p) = Started();
            p.HazardLevel[Fill] = -59.0;
            ToSealed(s, p);
            p.HazardLevel[Fill] = -57.0;
            Fire(s, p, MatronScript.TimerPoll);
            p.HazardLevel.Remove(Fill);

            Clear(p);
            s.RestartPhase(p);
            Assert.AreEqual(MatronStep.Sealed, s.Step);
            Assert.IsFalse(p.DoorOpen[Door(s, p)]);
            Assert.AreEqual(-59.0, StartZ(p.Hazards.Last()), 1e-9, "the acid restarts at the cycle's checkpoint");
            Assert.AreEqual(0.0, StartRate(p.Hazards.Last()), 1e-9);
            Assert.IsTrue(p.Immune[p.Head]);
            Assert.AreEqual(0, p.Props.Count);

            // the returning owner lands at the drop point: the wave and the lifted immunity follow
            p.OwnerAt = MatronData.ArrivalPoint;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Fighting, s.Step);
            Assert.IsFalse(p.Immune[p.Head]);
        }

        [TestMethod]
        public void Restart_DuringWave_KeepsSealsAndAddsAndRestartsAtTheCheckpoint()
        {
            var (s, p) = Started();
            p.HazardLevel[Fill] = -59.0;
            ToFighting(s, p, 1, clearWaves: false);
            Assert.AreEqual(6, p.Spawns.Count);
            // the acid rose after the checkpoint: the death must roll it back, not keep it
            p.HazardLevel[Fill] = -57.0;
            Fire(s, p, MatronScript.TimerPoll);
            p.HazardLevel.Remove(Fill);

            Clear(p);
            s.RestartPhase(p);
            Assert.AreEqual(MatronStep.Fighting, s.Step);
            Assert.AreEqual(1, s.FlowsDone);
            Assert.AreEqual(0, p.Spawns.Count, "living adds are the core's: the script spawns none again");
            Assert.AreEqual(0, p.Props.Count + p.DestroyedProps.Count, "sealed pits stay sealed");
            Assert.AreEqual(-59.0, StartZ(p.Hazards.Last()), 1e-9);
            Assert.AreEqual(0.0, StartRate(p.Hazards.Last()), 1e-9);
            CollectionAssert.Contains(p.Engaged, p.Head);
            Assert.AreEqual(6, s.WaveAdds.Count, "the restart keeps tracking the living wave");
        }

        [TestMethod]
        public void Restart_AtTheFloor_WithNoFlow_StartsNoFillFilm()
        {
            var (s, p) = Started();
            s.RestartPhase(p);
            Assert.AreEqual(0, FillStarts(p).Count());
        }

        [TestMethod]
        public void Threshold_WhileDraining_CheckpointsTheFloor()
        {
            var (s, p) = Started();
            ToFighting(s, p, 1);
            p.HazardLevel[Fill] = -57.0;
            Use(s, p, MatronScript.TagDrain);
            p.HazardLevel[Fill] = -58.0;   // still draining when her next threshold fires
            ToRunning(s, p);
            Assert.AreEqual(S.FillFloorZ, s.Checkpoint, 1e-9);
        }

        [TestMethod]
        public void Wave_SkipsSpawnPointsWithin5mOfTheOwner_AndUsesTheAddTier()
        {
            var (s, p) = Started();
            ToSealed(s, p);
            var near = MatronData.WaveSpawnPoints[0];
            p.OwnerAt = new PinnaclePoint(near.Cell, near.X, near.Y, -59.995f);
            Fire(s, p, MatronScript.TimerPoll);

            Assert.AreEqual(6, p.Spawns.Count);
            foreach (var sp in p.Spawns)
            {
                var dx = sp.At.X - near.X;
                var dy = sp.At.Y - near.Y;
                Assert.IsTrue(Math.Sqrt(dx * dx + dy * dy) >= 5.0, $"{sp.Tag} at {sp.At}");
            }

            var wave = MatronData.Wave(1);
            CollectionAssert.AreEquivalent(wave.SelectMany(w => Enumerable.Repeat(w.Wcid, w.Count)).ToArray(), p.Spawns.Select(x => x.Wcid).ToArray());
        }

        [TestMethod]
        public void OnStart_SaysNarrationThenBothTaunts_InOrder()
        {
            var p = new FakePinnacleFightPort();
            new MatronScript().OnStart(p);

            CollectionAssert.AreEqual(new[] { MatronLines.LineStart, MatronLines.LineTauntIntrude, MatronLines.LineTauntFeed }, p.Said);
        }

        [TestMethod]
        public void Rumble_SaysTheBurnTauntOnce_RightAfterTheRumbleLine()
        {
            var (s, p) = InFinaleOpening();
            Clear(p);
            Fire(s, p, MatronScript.TimerRumble);

            CollectionAssert.AreEqual(new[] { MatronLines.LineRumble, MatronLines.LineTauntBurn }, p.Said.Take(2).ToArray());
            Assert.AreEqual(1, p.Said.Count(l => l == MatronLines.LineTauntBurn), "the burn taunt, once");
        }
        [TestMethod]
        public void Rumble_OpensAllFourPits_NorthPlatforms_LethalScale_FinaleRate()
        {
            var (s, p) = InFinaleOpening();
            Clear(p);
            Fire(s, p, MatronScript.TimerRumble);

            Assert.AreEqual(MatronStep.FinaleRace, s.Step);
            foreach (var pit in MatronData.PitIds)
                Assert.IsTrue(p.DoorOpen[Live(p, MatronScript.DoorTag(pit))], $"pit {pit}");
            Assert.AreEqual(4 * 6, p.Props.Count(x => x.Tag.StartsWith("strip-", StringComparison.Ordinal)), "the four reopened pits' wall strips (four panels and two fog hosts each)");

            Assert.AreEqual(8, p.Props.Count(x => x.Tag.StartsWith("niche-floor", StringComparison.Ordinal)), "the reopened pits' niche acid floors");

            // the north door gives way to the rock: an add on the north sill at the rock tier, scale left to the weenie
            var north = MatronData.Opening(MatronData.NorthId);
            Assert.AreEqual(1, p.DestroyedProps.Count(h => AllProps(p).Any(x => x.Result == h && x.Tag == MatronScript.DoorTag(MatronData.NorthId))));
            var rock = p.Spawns.Single();
            Assert.AreEqual((MatronScript.TagRock, MatronData.RockWcid, MatronData.RockTier), (rock.Tag, rock.Wcid, rock.Tier));
            Assert.AreEqual(MatronLayout.Rock(north), rock.At);
            Assert.AreEqual((north.NicheCell, -48f), (rock.At.Cell, rock.At.Z));
            Assert.AreEqual(4, p.Props.Count(x => x.Prop.Wcid == MatronData.PlatformWcid));
            Assert.IsTrue(p.Props.Where(x => x.Prop.Wcid == MatronData.PlatformWcid).All(x => x.Tag.StartsWith("platform-3-", StringComparison.Ordinal)));

            var scale = MatronSettings.LevelScale(p.Level, S.HazardPctMin, S.HazardPctMax) * S.FinaleDamageScale;
            var fill = FillStarts(p).Last();
            Assert.AreEqual(S.FinaleRate, StartRate(fill), 1e-9);
            Assert.AreEqual(scale, fill.Args.GetDouble("damage_scale", 0, 0, 100), 1e-9);
            Assert.AreEqual(MatronLines.LineRumble, p.Said.First());
        }

        [TestMethod]
        public void Finale_RestartInRace_KeepsTheLethalScale()
        {
            var (s, p) = InFinaleOpening();
            Fire(s, p, MatronScript.TimerRumble);
            Clear(p);
            s.RestartPhase(p);
            var fill = FillStarts(p).Single();
            Assert.AreEqual(S.FinaleRate, StartRate(fill), 1e-9);
            Assert.AreEqual(MatronSettings.LevelScale(p.Level, S.HazardPctMin, S.HazardPctMax) * S.FinaleDamageScale, fill.Args.GetDouble("damage_scale", 0, 0, 100), 1e-9);
            Assert.IsTrue(p.Peaceful[p.Head] && p.Immune[p.Head], "she stands immune and peaceful");
            Assert.AreEqual(0, p.Spawns.Count, "the living rock is an add the hold kept: no second rock");
            Assert.AreEqual(0, p.Motions.Count + p.Engaged.Count);

            // the kept rock still lets the lava out
            BreakRock(s, p);
            Assert.AreEqual(MatronStep.FinaleLava, s.Step);
        }

        [TestMethod]
        public void Finale_RockThatFailsToSpawn_StillLetsTheLavaOut()
        {
            var (s, p) = InFinaleOpening();
            Fire(s, p, MatronScript.TimerRumble);
            // the core reports a spawn that failed twice as AddDied(tag) with no handle
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = MatronScript.TagRock }, p);
            Assert.AreEqual(MatronStep.FinaleLava, s.Step);
        }

        [TestMethod]
        public void Finale_StartsAtTheLevelThePhaseBeganAt()
        {
            var (s, p) = Started();
            ToFighting(s, p, 4);
            p.HazardLevel[Fill] = -56.5;
            p.AtFloor.Add(p.Head);
            Fire(s, p, MatronScript.TimerPoll);
            p.HazardLevel.Remove(Fill);
            p.Phase = 2;
            s.EnterPhase(2, p);
            Assert.AreEqual(-56.5, StartZ(FillStarts(p).Last()), 1e-9);
            Assert.AreEqual(0.0, StartRate(FillStarts(p).Last()), 1e-9);
            Assert.IsTrue(p.Props.Where(x => x.Prop.Wcid == MatronData.PlatformWcid).All(x => !p.Alive.Contains(x.Result)), "no platform survives into the finale");
        }

        [TestMethod]
        public void Finale_DrainInRace_ThenLava_LavaRateWins()
        {
            var (s, p) = InFinaleOpening();
            Fire(s, p, MatronScript.TimerRumble);
            p.HazardLevel[Fill] = -55.0;
            Use(s, p, MatronScript.TagDrain);
            Assert.AreEqual(-1.0, LastRate(p));
            BreakRock(s, p);
            Assert.AreEqual(-2.0, LastRate(p));
        }

        [TestMethod]
        public void Finale_ArrivesByTimeout_TurnsToTheArenaCentre_PlaysThePitOpening_ThenRumbleTimer()
        {
            var (s, p) = Started();
            ToFighting(s, p, 4);
            p.AtFloor.Add(p.Head);
            Fire(s, p, MatronScript.TimerPoll);
            p.Phase = 2;
            s.EnterPhase(2, p);
            Clear(p);
            p.Now += MatronScript.MoveTimeout;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.FinaleOpening, s.Step);
            Assert.AreEqual(MatronData.FinalePoint, p.Turns.Single().Facing);
            Assert.AreEqual(0, p.Motions.Count);

            Fire(s, p, MatronScript.TimerFace);
            Assert.AreEqual(new FakePinnacleFightPort.MotionCall(p.Head, MotionStance.HandCombat, MotionCommand.SpecialAttack1, false), p.Motions.Single());
            Assert.AreEqual(PlayScript.BreatheAcid, p.Effects.Single().Script);
            Assert.AreEqual(MatronScript.RumbleSeconds, p.TimerSets.Last(t => t.Tag == MatronScript.TimerRumble).Seconds, 1e-9);
            Assert.IsTrue(p.Peaceful[p.Head] && p.Immune[p.Head]);
            Assert.IsFalse(EverFallen(p));

            // the finale point's rotation faces the arena centre (the drain point)
            var f = MatronData.FinalePoint;
            var fwd = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, new System.Numerics.Quaternion(f.QX, f.QY, f.QZ, f.QW));
            var to = System.Numerics.Vector2.Normalize(new System.Numerics.Vector2(MatronData.DrainPoint.X - f.X, MatronData.DrainPoint.Y - f.Y));
            Assert.AreEqual(to.X, fwd.X, 1e-4f);
            Assert.AreEqual(to.Y, fwd.Y, 1e-4f);
        }
        [TestMethod]
        public void HeadDeath_InAnyStep_Wins()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = PinnacleEvent.HeadTag, Handle = p.Head }, p);
            Assert.AreEqual(1, p.WinCount);
        }

        [TestMethod]
        public void AddDeath_ChangesNothing()
        {
            var (s, p) = Started();
            ToFighting(s, p, 1);
            Clear(p);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = p.Spawns.Count > 0 ? "wave1-0" : "x", Handle = new PinnacleHandle(500) }, p);
            Assert.AreEqual(0, p.Calls.Count);
            Assert.AreEqual(0, p.WinCount);
        }

        [TestMethod]
        public void Poll_ReArmsItself()
        {
            var (s, p) = Started();
            Clear(p);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual((MatronScript.TimerPoll, MatronScript.PollSeconds), p.TimerSets.Last());
        }

        [TestMethod]
        public void Tunables_Matron_IsTheDefaultsWithTheLiveHazardClamp()
        {
            var (min, max) = PinnacleTunables.HazardPctClamp();
            var m = PinnacleTunables.Matron();
            Assert.AreEqual(MatronSettings.Defaults with { HazardPctMin = min, HazardPctMax = max }, m);
        }

        [TestMethod]
        public void Script_DataAndShape()
        {
            var s = new MatronScript();
            Assert.AreEqual("matron", s.Kind);
            Assert.AreEqual(2, s.PhaseCount);
            Assert.AreEqual(MatronData.HeadWcid, s.Data.HeadWcid);
            Assert.AreEqual(MatronData.HeadTier, s.Data.HeadTier);
            Assert.AreEqual(HazardRegionPresets.MatronName, s.Data.HazardRegionPreset);
            Assert.AreSame(MatronData.ExcludeRows, s.Data.ExcludeRows);
            Assert.AreEqual(MatronData.FightEntryPoint, s.Data.FightEntryPoint);
            Assert.AreEqual(MatronData.CachePoint, s.Data.CachePoint);
            Assert.AreEqual(MatronData.ReturnPortalPoint, s.Data.ReturnPortalPoint);
            Assert.IsFalse(string.IsNullOrEmpty(s.Describe()));
        }

        // ================================================================ the poll reconciles the fill (review F1, F2)

        [TestMethod]
        public void Poll_ResendsTheRateAndScale_EveryPoll_EvenWhenUnchanged()
        {
            var (s, p) = Started();
            ToSealed(s, p);
            Assert.AreEqual((Fill, "rate", 0.0), p.Controls.Last(), "the seal sent 0");

            // the core's retry may since have relaunched the run with its original 0.03: the next poll corrects it blind
            var before = p.Controls.Count;
            Fire(s, p, MatronScript.TimerPoll);
            var sent = p.Controls.Skip(before).ToList();
            Assert.IsTrue(sent.Contains((Fill, "rate", 0.0)), "rate re-sent although the last accepted one was 0");
            Assert.IsTrue(sent.Any(c => c.Key == "damage_scale"), "the damage scale is re-sent too");

            before = p.Controls.Count;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(1, p.Controls.Skip(before).Count(c => c.Key == "rate"), "and again on every poll");
        }

        [TestMethod]
        public void Poll_AfterTheEndRetriesRunOut_StartsTheFillOnceMore_WithinItsCooldownAndCap()
        {
            var (s, p) = Started();
            ToFlowing(s, p);

            void End() => s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.HazardEnded, Tag = Fill, Outcome = HazardOutcome.Aborted }, p);

            for (var i = 0; i < 6; i++)
                End();   // five immediate restarts, the sixth end leaves it off

            var starts = FillStarts(p).Count();
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(starts + 1, FillStarts(p).Count(), "an unsealed flow is never left without acid");
            AssertNeverTwoFills(p, "poll restart");

            // it ends again at once: the cooldown holds the next poll restart back
            End();
            p.Now += 1;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(starts + 1, FillStarts(p).Count(), "inside the cooldown");

            // the cap: with every restart ending at once and no live read between them, at most the cap in all
            for (var i = 0; i < 30; i++)
            {
                p.Now += MatronScript.FillReconcileCooldown;
                Fire(s, p, MatronScript.TimerPoll);
                End();
            }

            Assert.AreEqual(starts + MatronScript.MaxPollFillRestarts, FillStarts(p).Count(), "the per-phase cap holds");

            // a live read resets the count
            p.HazardLevel[Fill] = -59.0;
            Fire(s, p, MatronScript.TimerPoll);
            p.HazardLevel.Remove(Fill);
            End();
            p.Now += MatronScript.FillReconcileCooldown;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(starts + MatronScript.MaxPollFillRestarts + 1, FillStarts(p).Count(), "a successful read resets the cap");
            AssertNeverTwoFills(p, "whole sequence");
        }

        [TestMethod]
        public void Poll_DoesNotRestartAFillThatIsNotWanted()
        {
            var (s, p) = Started();
            for (var i = 0; i < 5; i++)
            {
                p.Now += MatronScript.FillReconcileCooldown;
                Fire(s, p, MatronScript.TimerPoll);
            }

            Assert.AreEqual(0, FillStarts(p).Count(), "no flow, no acid: nothing to reconcile");
        }

        [TestMethod]
        public void Poll_SnapsBackASurfaceThatFell_WhenNothingLowersIt()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            p.HazardLevel[Fill] = -55.0;
            Fire(s, p, MatronScript.TimerPoll);

            // a core retry relaunched the run at its original start_z
            p.HazardLevel[Fill] = -59.9;
            Clear(p);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.IsTrue(p.Controls.Contains((Fill, "set_z", -55.0)));
            Assert.AreEqual(-55.0, s.Level, 1e-9);

            // a drain lowers it on purpose: no snap
            Use(s, p, MatronScript.TagDrain);
            Clear(p);
            p.HazardLevel[Fill] = -58.0;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.IsFalse(p.Controls.Any(c => c.Key == "set_z"));
            Assert.AreEqual(-58.0, s.Level, 1e-9);
        }

        // ================================================================ the lava, the slay and the pillars

        private static (MatronScript S, FakePinnacleFightPort P) InLava(double acidZ)
        {
            var (s, p) = InFinaleOpening();
            Fire(s, p, MatronScript.TimerRumble);
            p.HazardLevel[Fill] = acidZ;
            Fire(s, p, MatronScript.TimerPoll);
            BreakRock(s, p);
            Assert.AreEqual(MatronStep.FinaleLava, s.Step);
            return (s, p);
        }

        [TestMethod]
        public void Slay_IsTheFixedDelay_WhateverTheAcid_AndOnlyThroughItsTimer()
        {
            var (s, p) = InLava(-46.0);
            Assert.AreEqual(5.0, MatronScript.PillarSeconds);
            Assert.AreEqual(1.0, MatronScript.SlaySeconds);

            // polls never slay her (the old level-and-cap rule is gone)
            for (var i = 0; i < 20; i++)
            {
                p.Now += 1;
                Fire(s, p, MatronScript.TimerPoll);
            }
            Assert.AreEqual(0, p.Slain.Count);

            Fire(s, p, MatronScript.TimerPillar);
            Assert.AreEqual(0, p.Slain.Count);
            Fire(s, p, MatronScript.TimerSlay);
            CollectionAssert.AreEqual(new[] { p.Head }, p.Slain);
            Fire(s, p, MatronScript.TimerSlay);
            Assert.AreEqual(1, p.Slain.Count, "once");
        }

        [TestMethod]
        public void Slay_Restart_InLava_BeforeTheSlay_RunsTheFullDelayAgain_LavaPropsKept()
        {
            var (s, p) = InLava(-50.0);
            p.Now += 3;
            p.HazardLevel.Remove(Fill);
            Clear(p);
            s.RestartPhase(p);

            Assert.AreEqual(MatronStep.FinaleLava, s.Step);
            Assert.AreEqual(MatronScript.PillarSeconds, p.TimerSets.Single(t => t.Tag == MatronScript.TimerPillar).Seconds, 1e-9);
            Assert.IsFalse(p.TimerSets.Any(t => t.Tag == MatronScript.TimerSlay));
            Assert.AreEqual(0, p.Props.Count + p.DestroyedProps.Count, "the lava props stay");
            Assert.IsTrue(FillStarts(p).Count() <= 1 && FillStarts(p).All(h => StartRate(h) == S.LavaClearRate), "any acid restarts under the lava's clear rate");
            Assert.IsTrue(p.Peaceful[p.Head] && p.Immune[p.Head]);
        }

        [TestMethod]
        public void Restart_AfterThePillar_BeforeTheSlay_RearmsTheSlay_NotThePillar()
        {
            var (s, p) = InLava(S.FillFloorZ);
            Fire(s, p, MatronScript.TimerPillar);
            Clear(p);
            s.RestartPhase(p);
            Assert.IsFalse(p.TimerSets.Any(t => t.Tag == MatronScript.TimerPillar));
            Assert.AreEqual(MatronScript.SlaySeconds, p.TimerSets.Single(t => t.Tag == MatronScript.TimerSlay).Seconds, 1e-9);
            Assert.AreEqual(0, p.Props.Count, "no second pillar");
        }

        // ================================================================ the brood gates each threshold (playtest round 6)

        [TestMethod]
        public void Threshold_WithAWaveAddAlive_HoldsGuarded_ImmuneStillFighting_NoWalk_UntilTheLastDies()
        {
            var (s, p) = Started();
            ToFighting(s, p, 1, clearWaves: false);
            var wave = s.WaveAdds.ToList();
            Assert.AreEqual(6, wave.Count, "the six wave-1 adds are tracked");
            Assert.IsTrue(wave.All(h => p.Spawns.Any(x => x.Result == h && x.Tag.StartsWith("wave1-", StringComparison.Ordinal))), "only wave adds are tracked");
            Assert.IsFalse(wave.Contains(p.Head));

            Clear(p);
            p.AtFloor.Add(p.Head);
            Fire(s, p, MatronScript.TimerPoll);

            Assert.AreEqual(MatronStep.Guarded, s.Step);
            Assert.IsTrue(p.Immune[p.Head], "immune at the threshold");
            Assert.AreEqual(0, p.CallsNamed(nameof(IPinnacleFightPort.SetPeaceful)).Count(), "never made peaceful: she keeps fighting");
            Assert.AreEqual(0, p.MoveTos.Count, "no walk while her brood lives");
            CollectionAssert.AreEqual(new[] { MatronLines.LineBroodGuards }, p.Said);

            // every add but one dies: still held, and the line is not said again
            foreach (var h in wave.Skip(1))
                p.Kill(h);
            Fire(s, p, MatronScript.TimerPoll);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Guarded, s.Step);
            Assert.AreEqual(0, p.MoveTos.Count);
            Assert.AreEqual(1, p.Said.Count(l => l == MatronLines.LineBroodGuards), "said once, when the hold begins");

            // the last dies: the next poll runs her to the next pit, as an ungated threshold would
            p.Kill(wave[0]);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Running, s.Step);
            Assert.IsTrue(p.Immune[p.Head] && p.Peaceful[p.Head]);
            Assert.AreEqual(MatronData.Opening(s.CurrentPit).Stand, p.MoveTos.Single().To);
            Assert.AreEqual(MatronLines.LineFlowOpens, p.Said.Last());
        }

        [TestMethod]
        public void Guarded_RepeatsTheBroodLineEvery20s_AndNeverTimesOut()
        {
            var (s, p) = InStep(MatronStep.Guarded);
            Assert.AreEqual(20.0, MatronScript.BroodReminderSeconds);
            var held = p.Now;
            int Lines() => p.Said.Count(l => l == MatronLines.LineBroodGuards);
            var atHold = Lines();
            var walks = p.MoveTos.Count;

            p.Now = held + MatronScript.BroodReminderSeconds - 0.5;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(atHold, Lines(), "not before 20 s");

            p.Now = held + MatronScript.BroodReminderSeconds;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(atHold + 1, Lines(), "again at 20 s");

            p.Now += 1.0;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(atHold + 1, Lines(), "the reminder counts from the last saying");

            // no cap and no timeout: ten minutes on she is still held and still reminding
            for (var i = 0; i < 30; i++)
            {
                p.Now += MatronScript.BroodReminderSeconds;
                Fire(s, p, MatronScript.TimerPoll);
            }
            Assert.AreEqual(MatronStep.Guarded, s.Step);
            Assert.AreEqual(atHold + 31, Lines());
            Assert.AreEqual(walks, p.MoveTos.Count, "no walk while held");

            // the brood dies: she runs, and no reminder follows
            KillWaves(s, p);
            p.Now += MatronScript.BroodReminderSeconds;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Running, s.Step);
            Assert.AreEqual(atHold + 31, Lines());
        }

        [TestMethod]
        public void Wave4_EverySentinelSpawnFailed_NoSentinelLine()
        {
            var (s, p) = Started();
            ToFighting(s, p, 3);
            ToSealed(s, p);
            p.FailNextSpawns = 2;
            p.OwnerAt = Floor(220f, -150f);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Fighting, s.Step);
            Assert.AreEqual(2, p.Spawns.Count(x => x.Tag.StartsWith("wave4-", StringComparison.Ordinal) && x.Result.IsNone), "both sentinel spawns failed");
            Assert.IsFalse(p.Said.Contains(MatronLines.LineSentinels), "no line for sentinels that never stood");
            Assert.IsFalse(p.Said.Contains(MatronLines.LineTauntSentinels), "no taunt for sentinels that never stood");

            // nothing guards her: the finale threshold passes at once
            p.AtFloor.Add(p.Head);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(1, p.AdvanceCount);
        }

        [TestMethod]
        public void Threshold_WithNoWaveAddAlive_RunsAtOnce_NoHold()
        {
            // the wave was killed
            var (s, p) = Started();
            ToFighting(s, p, 1);
            Clear(p);
            p.AtFloor.Add(p.Head);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Running, s.Step);
            CollectionAssert.AreEqual(new[] { MatronLines.LineFlowOpens }, p.Said, "no brood line");

            // every wave spawn failed twice (None): nothing to guard her
            var (s2, p2) = Started();
            ToSealed(s2, p2);
            p2.FailNextSpawns = 6;
            p2.OwnerAt = Floor(220f, -150f);
            Fire(s2, p2, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Fighting, s2.Step);
            Assert.AreEqual(0, s2.WaveAdds.Count, "a failed spawn is not tracked");
            p2.AtFloor.Add(p2.Head);
            Fire(s2, p2, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Running, s2.Step);
        }

        [TestMethod]
        public void FinalThreshold_TheSentinelsHoldTheFinale_AdvancesOnlyOnceBothAreDead()
        {
            var (s, p) = Started();
            ToFighting(s, p, 3);

            // the fourth cycle by hand, the sentinels left standing
            ToSealed(s, p);
            p.OwnerAt = Floor(220f, -150f);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Fighting, s.Step);
            Assert.AreEqual(4, s.FlowsDone);

            // wave 4: exactly the two great sentinels, at the Boss tier, with their line; waves 1-3 stayed Elite
            var fourth = p.Spawns.Where(x => x.Tag.StartsWith("wave4-", StringComparison.Ordinal)).ToList();
            Assert.AreEqual(2, fourth.Count);
            Assert.IsTrue(fourth.All(x => x.Wcid == 1007558u && x.Tier == PinnacleTier.Boss), "two 1007558 at Boss");
            Assert.AreEqual(fourth.Count, p.Spawns.Count(x => x.Tier == PinnacleTier.Boss), "nothing else at Boss");
            Assert.AreEqual(6 + 6 + 7, p.Spawns.Count(x => x.Tier == PinnacleTier.Elite && x.Tag.StartsWith("wave", StringComparison.Ordinal)), "waves 1-3 unchanged and Elite");
            Assert.AreEqual(1, p.Said.Count(l => l == MatronLines.LineSentinels), "the sentinel line, once");
            Assert.AreEqual(1, p.Said.Count(l => l == MatronLines.LineTauntSentinels), "the sentinel taunt, once");
            Assert.AreEqual(p.Said.IndexOf(MatronLines.LineSentinels) + 1, p.Said.IndexOf(MatronLines.LineTauntSentinels), "the taunt directly follows the sentinel line");

            var sentinels = fourth.Select(x => x.Result).ToList();
            p.AtFloor.Add(p.Head);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(MatronStep.Guarded, s.Step);
            Assert.AreEqual(0, p.AdvanceCount, "no phase change while a sentinel stands: the core would destroy it");
            Assert.IsTrue(p.Immune[p.Head]);

            p.Kill(sentinels[0]);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(0, p.AdvanceCount, "one sentinel still stands");

            p.Kill(sentinels[1]);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(1, p.AdvanceCount, "both dead: the finale");
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(1, p.AdvanceCount, "asked once");
        }

        [TestMethod]
        public void Guarded_RestartKeepsTheHold_AndAPhaseEntryForgetsTheBrood()
        {
            var (s, p) = InStep(MatronStep.Guarded);
            p.Immune[p.Head] = false;
            Clear(p);
            s.RestartPhase(p);
            Assert.AreEqual(MatronStep.Guarded, s.Step);
            Assert.IsTrue(p.Immune[p.Head]);
            CollectionAssert.Contains(p.Engaged, p.Head, "still fighting");
            Assert.AreEqual(0, p.MoveTos.Count + p.Spawns.Count);
            Assert.AreEqual(6, s.WaveAdds.Count);

            // an admin jump: the core cleared the adds, the script forgets them
            s.EnterPhase(1, p);
            Assert.AreEqual(0, s.WaveAdds.Count);
            Assert.AreEqual(MatronStep.Fighting, s.Step);

            var (s2, p2) = InStep(MatronStep.Guarded);
            p2.Phase = 2;
            s2.EnterPhase(2, p2);
            Assert.AreEqual(0, s2.WaveAdds.Count);
        }

        [TestMethod]
        public void WaveData_FinalWaveIsTwoSentinels_TiersByWave()
        {
            CollectionAssert.AreEqual(new[] { (1007558u, 2) }, MatronData.Wave(4).ToArray());
            Assert.AreEqual(1007558u, MatronData.SentinelWcid);
            CollectionAssert.AreEqual(new[] { PinnacleTier.Elite, PinnacleTier.Elite, PinnacleTier.Elite, PinnacleTier.Boss }, Enumerable.Range(1, 4).Select(MatronData.WaveTier).ToArray());
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MatronData.WaveTier(5));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MatronData.WaveTier(0));
        }

        // ================================================================ the flow delay (playtest round 6)

        [TestMethod]
        public void FlowDelay_FirstFlow_NoFillUntilTheDelayIsOver_ThenTheFlowRate()
        {
            var (s, p) = Started();
            Assert.AreEqual(2.0, S.FlowDelay, "two seconds by default (playtest round 6 follow-up: 5 s felt too slow)");

            ToDoorOpen(s, p);
            var opened = p.Now;
            Assert.IsTrue(s.FlowWaiting);
            Assert.IsTrue(p.DoorOpen[Door(s, p)], "the door is open");
            Assert.AreEqual(0, FillStarts(p).Count(), "no acid the instant the door opens");
            Assert.AreEqual(S.FlowDelay, p.TimerSets.Last(t => t.Tag == MatronScript.TimerFlowStart).Seconds, 1e-9);

            // the poll's reconciler starts nothing during the delay either
            for (var t = 0.5; t < S.FlowDelay; t += 0.5)
            {
                p.Now = opened + t;
                Fire(s, p, MatronScript.TimerPoll);
            }
            Assert.AreEqual(0, FillStarts(p).Count(), "nothing visible before the delay passes");

            p.Now = opened + S.FlowDelay;
            Fire(s, p, MatronScript.TimerFlowStart);
            Assert.IsFalse(s.FlowWaiting);
            var fill = FillStarts(p).Single();
            Assert.AreEqual(S.FlowRate, StartRate(fill), 1e-9);
            Assert.AreEqual(S.FillFloorZ, StartZ(fill), 1e-9);

            // the escalation counts from the delayed start, not from the door
            p.Now = opened + S.FlowEscalateSeconds + 1.0;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(S.FlowRate, LastRate(p), 1e-9, "46 s after the door, 44 s after the start: not doubled yet");
            p.Now = opened + S.FlowDelay + S.FlowEscalateSeconds;
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(S.FlowRate * 2, LastRate(p), 1e-9);
        }

        [TestMethod]
        public void FlowDelay_Zero_StartsTheFillWithTheDoor()
        {
            var (s, p) = Started(settings: S with { FlowDelay = 0 });
            ToDoorOpen(s, p);
            Assert.IsFalse(s.FlowWaiting);
            Assert.AreEqual(1, FillStarts(p).Count());
            Assert.IsFalse(p.TimerSets.Any(t => t.Tag == MatronScript.TimerFlowStart));
        }

        [TestMethod]
        public void FlowDelay_SealInsideTheDelay_SpendsTheStart_AndAStaleStartIsIgnored()
        {
            var (s, p) = Started();
            ToDoorOpen(s, p);
            p.OwnerAt = Sill(MatronData.Opening(s.CurrentPit));
            Use(s, p, MatronScript.DoorTag(s.CurrentPit));
            Assert.AreEqual(MatronStep.Sealed, s.Step);
            Assert.IsFalse(s.FlowWaiting);
            CollectionAssert.Contains(p.CancelledTimers, MatronScript.TimerFlowStart);

            Fire(s, p, MatronScript.TimerFlowStart);
            Assert.AreEqual(0, FillStarts(p).Count(), "a sealed flow never starts its acid");
        }

        [TestMethod]
        public void FlowDelay_RestartInsideTheDelay_WaitsAFullDelayAgain_Idempotently()
        {
            var (s, p) = Started();
            ToDoorOpen(s, p);
            p.Now += S.FlowDelay / 2;   // inside the delay: the original start would land FlowDelay / 2 from now
            Clear(p);

            s.RestartPhase(p);
            var first = Snapshot(s, p);
            s.RestartPhase(p);
            Assert.AreEqual(first, Snapshot(s, p));

            Assert.IsTrue(s.FlowWaiting);
            Assert.AreEqual(MatronStep.Flowing, s.Step);
            Assert.AreEqual(p.Now + S.FlowDelay, p.Timers[MatronScript.TimerFlowStart], 1e-9, "a full delay from the return");
            Assert.AreEqual(0, FillStarts(p).Count(), "still nothing at the floor");

            p.Now += S.FlowDelay;
            Fire(s, p, MatronScript.TimerFlowStart);
            Assert.AreEqual(S.FlowRate, StartRate(FillStarts(p).Single()), 1e-9);
        }

        [TestMethod]
        public void FlowDelay_LaterFlow_AcidAlreadyUp_StaysStillUntilTheStart_AndADrainInTheDelayDrains()
        {
            var (s, p) = Started();
            p.HazardLevel[Fill] = -58.0;   // acid left from the first cycle
            ToFighting(s, p, 1);
            ToDoorOpen(s, p);
            Assert.IsTrue(s.FlowWaiting);
            Fire(s, p, MatronScript.TimerPoll);
            Assert.AreEqual(0.0, LastRate(p), 1e-9, "the standing acid does not rise during the delay");

            Use(s, p, MatronScript.TagDrain);
            Assert.IsTrue(s.Draining);
            Assert.AreEqual(S.DrainRate, LastRate(p), 1e-9, "a drain in the delay drains");

            // the delay ends while the drain still runs: the drain still beats the flow rate
            p.Now += S.FlowDelay;
            Fire(s, p, MatronScript.TimerFlowStart);
            Assert.AreEqual(S.DrainRate, LastRate(p), 1e-9);

            p.HazardLevel[Fill] = S.FillFloorZ;
            Fire(s, p, MatronScript.TimerDrainDone);
            Assert.AreEqual(S.FlowRate, LastRate(p), 1e-9, "the drain over, the flow rises from the delayed start");
        }

        [TestMethod]
        public void FlowDelay_OverrideKey_ParsesAndIsBounded()
        {
            Assert.IsTrue(MatronOverrides.TryCreateScript(S, HazardArgs.Parse("flow_delay=12.5"), out var script, out var error), error);
            Assert.AreEqual(12.5, script.Settings.FlowDelay);
            Assert.IsTrue(MatronOverrides.TryApply(S, HazardArgs.Parse("flow_delay=0"), out var none, out error), error);
            Assert.AreEqual(0.0, none.FlowDelay);
            Assert.IsTrue(MatronOverrides.TryApply(S, HazardArgs.Parse("flow_delay=30"), out var max, out error), error);
            Assert.AreEqual(30.0, max.FlowDelay);

            foreach (var bad in new[] { "flow_delay=-0.5", "flow_delay=30.5", "flow_delay=soon" })
            {
                Assert.IsFalse(MatronOverrides.TryApply(S, HazardArgs.Parse(bad), out var kept, out var refusal), bad);
                StringAssert.Contains(refusal, "flow_delay", bad);
                Assert.AreEqual(S, kept);
            }

            // the per-copy delay reaches the fight
            var (s, p) = Started(settings: script.Settings);
            ToDoorOpen(s, p);
            Assert.AreEqual(12.5, p.TimerSets.Last(t => t.Tag == MatronScript.TimerFlowStart).Seconds, 1e-9);
        }

        // ================================================================ step fixtures and checks

        private static (MatronScript S, FakePinnacleFightPort P) InStep(MatronStep step)
        {
            switch (step)
            {
                case MatronStep.Fighting:
                {
                    var (s, p) = Started();
                    p.HazardLevel[Fill] = -59.0;
                    ToFighting(s, p, 1);
                    return (s, p);
                }
                case MatronStep.Guarded:
                {
                    var (s, p) = Started();
                    ToFighting(s, p, 1, clearWaves: false);
                    p.AtFloor.Add(p.Head);
                    Fire(s, p, MatronScript.TimerPoll);
                    p.AtFloor.Remove(p.Head);
                    Assert.AreEqual(MatronStep.Guarded, s.Step);
                    return (s, p);
                }
                case MatronStep.Running:
                {
                    var (s, p) = Started();
                    ToRunning(s, p);
                    return (s, p);
                }
                case MatronStep.Opening:
                {
                    var (s, p) = Started();
                    ToOpening(s, p);
                    return (s, p);
                }
                case MatronStep.Flowing:
                {
                    var (s, p) = Started();
                    ToFlowing(s, p);
                    return (s, p);
                }
                case MatronStep.Sealed:
                {
                    var (s, p) = Started();
                    ToSealed(s, p);
                    return (s, p);
                }
                case MatronStep.FinaleWalking:
                {
                    var (s, p) = Started();
                    ToFighting(s, p, 4);
                    p.AtFloor.Add(p.Head);
                    Fire(s, p, MatronScript.TimerPoll);
                    p.Phase = 2;
                    s.EnterPhase(2, p);
                    return (s, p);
                }
                case MatronStep.FinaleOpening:
                    return InFinaleOpening();
                case MatronStep.FinaleRace:
                {
                    var (s, p) = InFinaleOpening();
                    Fire(s, p, MatronScript.TimerRumble);
                    return (s, p);
                }
                case MatronStep.FinaleLava:
                {
                    var (s, p) = InFinaleOpening();
                    Fire(s, p, MatronScript.TimerRumble);
                    BreakRock(s, p);
                    return (s, p);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(step));
            }
        }

        /// <summary>What a restart must leave identical: the script's own state and the world state it asserts.</summary>
        private static string Snapshot(MatronScript s, FakePinnacleFightPort p)
        {
            var lastFill = FillStarts(p).LastOrDefault();
            return string.Join("|",
                s.Describe(),
                s.Step,
                s.Checkpoint.ToString("R"),
                s.DrainSpent,
                s.Draining,
                LiveProps(p),
                p.Peaceful.TryGetValue(p.Head, out var pc) ? pc.ToString() : "-",
                p.Immune.TryGetValue(p.Head, out var im) ? im.ToString() : "-",
                p.Floors.TryGetValue(p.Head, out var fl) ? fl?.ToString("R") : "-",
                lastFill?.Args.ToString() ?? "-",
                string.Join(",", p.Timers.OrderBy(t => t.Key).Select(t => $"{t.Key}@{t.Value:R}")));
        }

        // ================================================================ the drain beacon

        private static IEnumerable<FakePinnacleFightPort.PropCall> Beacons(FakePinnacleFightPort p) => AllProps(p).Where(x => x.Tag == MatronScript.TagDrainBeacon);

        private static int LiveBeacons(FakePinnacleFightPort p) => Beacons(p).Count(x => p.Alive.Contains(x.Result));

        private static void PollAt(MatronScript s, FakePinnacleFightPort p, double depth)
        {
            p.HazardLevel[Fill] = s.Settings.FillFloorZ + depth;
            Fire(s, p, MatronScript.TimerPoll);
        }

        [TestMethod]
        public void Beacon_AbsentBelowTheDepth_SpawnedAtIt_WithTheTag_TheScript_AndTheDrainsXY()
        {
            var (s, p) = Started();
            ToFlowing(s, p);

            PollAt(s, p, 1.9);
            Assert.AreEqual(0, Beacons(p).Count(), "under 2 m of acid: no beacon");

            PollAt(s, p, 2.5);
            var b = Beacons(p).Single();
            Assert.AreEqual(MatronScript.TagDrainBeacon, b.Tag);
            Assert.AreEqual("drain-beacon", MatronScript.TagDrainBeacon);
            Assert.AreEqual(MatronData.EffectHostWcid, b.Prop.Wcid);
            Assert.AreEqual(0x3300079Au, b.Prop.Script);
            Assert.AreEqual(MatronData.DrainPoint.Cell, b.Prop.At.Cell);
            Assert.AreEqual(MatronData.DrainPoint.X, b.Prop.At.X);
            Assert.AreEqual(MatronData.DrainPoint.Y, b.Prop.At.Y);
            Assert.AreEqual(MatronLayout.DrainZ + MatronLayout.BeaconLift, b.Prop.At.Z);
            Assert.AreEqual(MatronLayout.DrainBeacon(0x3300079Au), b.Prop);
            Assert.IsTrue(p.Alive.Contains(b.Result));
        }

        [TestMethod]
        public void Beacon_Host_IsTurnedSoTheHangingBeamRises()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            PollAt(s, p, 3.0);

            var at = Beacons(p).Single().Prop.At;
            var turned = ACE.Server.HazardMechanics.MemoryLayout.FloorOrientation(0x3300079Au);

            Assert.IsTrue(ACE.Server.HazardMechanics.HazardDefaults.HangsBelowHost(0x3300079Au));
            Assert.AreEqual(turned.W, at.QW, 1e-6);
            Assert.AreEqual(turned.X, at.QX, 1e-6);
            Assert.AreEqual(turned.Y, at.QY, 1e-6);
            Assert.AreEqual(turned.Z, at.QZ, 1e-6);
            Assert.IsTrue(Math.Abs(at.QX) > 0.99, "a half turn about X: the identity orientation would draw the beam under the floor");
        }

        [TestMethod]
        public void Beacon_SpawnedOnceAcrossRepeatedPolls()
        {
            var (s, p) = Started();
            ToFlowing(s, p);

            for (var i = 0; i < 6; i++)
                PollAt(s, p, 3.0 + i * 0.1);

            Assert.AreEqual(1, Beacons(p).Count());
            Assert.AreEqual(1, LiveBeacons(p));
        }

        [TestMethod]
        public void Beacon_UsingTheDrain_DestroysIt_AndItNeverReturns_EvenAfterARestartAndRisingAcid()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            PollAt(s, p, 3.0);
            var b = Beacons(p).Single().Result;

            Use(s, p, MatronScript.TagDrain);
            Assert.IsTrue(s.DrainSpent);
            Assert.IsFalse(p.Alive.Contains(b), "the accepted drain use destroys the beacon at once, before the next poll");
            CollectionAssert.Contains(p.DestroyedProps, b);

            Fire(s, p, MatronScript.TimerDrainDone);
            PollAt(s, p, 4.0);
            Assert.AreEqual(0, LiveBeacons(p), "spent: no beacon while the acid is deep again");

            s.RestartPhase(p);
            PollAt(s, p, 5.0);
            Assert.AreEqual(1, Beacons(p).Count(), "a death restart does not bring it back");
            Assert.AreEqual(0, LiveBeacons(p));
        }

        [TestMethod]
        public void Beacon_RestartPhase_NeverSpawnsOrDestroysIt()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            PollAt(s, p, 3.0);
            var b = Beacons(p).Single().Result;
            Clear(p);

            s.RestartPhase(p);
            Assert.IsFalse(p.Props.Any(x => x.Tag == MatronScript.TagDrainBeacon), "a restart spawns no beacon");
            Assert.IsFalse(p.DestroyedProps.Contains(b), "a restart destroys no beacon");
            Assert.IsTrue(p.Alive.Contains(b));
        }

        [TestMethod]
        public void Beacon_RefusedDrainUse_LeavesTheStateUnchanged()
        {
            // a beacon depth under the drain's own quarter-metre floor makes the beacon show where the drain still refuses
            var (s, p) = Started(settings: S with { BeaconDepth = 0.1 });
            ToFlowing(s, p);
            PollAt(s, p, 0.2);
            var b = Beacons(p).Single().Result;
            var controls = p.Controls.Count;

            Use(s, p, MatronScript.TagDrain);

            Assert.AreEqual(MatronLines.LineDrainEmpty, p.Said.Last());
            Assert.IsFalse(s.DrainSpent);
            Assert.IsFalse(s.Draining);
            Assert.IsTrue(p.Alive.Contains(b), "a refused use does not destroy the beacon");
            Assert.AreEqual(controls, p.Controls.Count);
        }

        [TestMethod]
        public void Beacon_SpentDrainRefusal_DoesNotTouchABeacon()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            p.HazardLevel[Fill] = S.FillFloorZ + 2.0;
            Use(s, p, MatronScript.TagDrain);
            var destroyed = p.DestroyedProps.Count;

            Use(s, p, MatronScript.TagDrain);

            Assert.AreEqual(MatronLines.LineDrainSpent, p.Said.Last());
            Assert.AreEqual(destroyed, p.DestroyedProps.Count);
        }

        [TestMethod]
        public void Beacon_DestroyedAtFinaleLava()
        {
            var (s, p) = InFinaleOpening();
            Fire(s, p, MatronScript.TimerRumble);
            Assert.AreEqual(MatronStep.FinaleRace, s.Step);

            PollAt(s, p, 3.0);
            var b = Beacons(p).Single().Result;
            Assert.IsTrue(p.Alive.Contains(b), "deep acid in the race and the drain unused: the beacon shows");

            BreakRock(s, p);
            Assert.AreEqual(MatronStep.FinaleLava, s.Step);
            PollAt(s, p, 3.0);

            Assert.IsFalse(p.Alive.Contains(b), "the lava clears the acid itself: the drain has nothing to do");
            Assert.AreEqual(0, LiveBeacons(p));
        }

        [TestMethod]
        public void Beacon_DestroyedByEnteringTheLoopAgain_TheCoreDestroysTheRestAtTheFightsEnd()
        {
            // the script's own teardown (DestroyAllProps) covers the beacon; the core's End destroys every tracked prop (PinnacleFight.End)
            var (s, p) = Started();
            ToFlowing(s, p);
            PollAt(s, p, 3.0);
            var b = Beacons(p).Single().Result;

            s.EnterPhase(1, p);

            Assert.IsFalse(p.Alive.Contains(b));
            CollectionAssert.Contains(p.DestroyedProps, b);
            Assert.AreEqual(0, LiveBeacons(p));
        }

        [TestMethod]
        public void Beacon_FailedSpawn_IsRetriedAfterTheCooldown_NotEveryPoll()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            p.FailNextSpawns = 1;

            PollAt(s, p, 3.0);
            PollAt(s, p, 3.0);
            PollAt(s, p, 3.0);
            Assert.AreEqual(1, Beacons(p).Count(), "one failed attempt, no retry inside the cooldown");

            p.Now += MatronScript.BeaconRetrySeconds;
            PollAt(s, p, 3.0);
            Assert.AreEqual(2, Beacons(p).Count());
            Assert.AreEqual(1, LiveBeacons(p));
        }

        [TestMethod]
        public void Beacon_FailedSpawn_DepthFlapInsideTheCooldown_DoesNotRetry()
        {
            var (s, p) = Started();
            ToFlowing(s, p);
            p.FailNextSpawns = 1;

            PollAt(s, p, 3.0);
            Assert.AreEqual(1, Beacons(p).Count(), "the failed attempt");

            // the acid is under the depth for one poll. The fill run ended (Stopped), so the poll reads the level afresh instead of
            // snapping a fallen surface back to the last known one
            p.HazardLevel.Remove(Fill);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.HazardEnded, Tag = Fill, Outcome = HazardOutcome.Stopped }, p);
            PollAt(s, p, 1.0);
            Assert.AreEqual(0, LiveBeacons(p));
            PollAt(s, p, 3.0);
            Assert.AreEqual(1, Beacons(p).Count(), "inside the cooldown a depth flap does not bring a second attempt");

            p.Now += MatronScript.BeaconRetrySeconds;
            PollAt(s, p, 3.0);
            Assert.AreEqual(2, Beacons(p).Count());
        }

        [TestMethod]
        public void Beacon_LiftClearsTheTurnedEffectHostsSphere_DatFree()
        {
            // the Effect Host (Setup 0x02000176) sphere centre is 0.136 m over its origin (TurnedHostLiftTests); turned, it falls that far under it
            Assert.IsTrue(MatronLayout.BeaconLift >= 2 * 0.136f);
            Assert.AreEqual(MatronLayout.DrainZ + MatronLayout.BeaconLift, MatronLayout.DrainBeacon(MatronLayout.DrainBeacons[0]).At.Z, 1e-6);
        }

        [TestMethod]
        public void Beacon_Overrides_DepthAndFx_AcceptAndRejectAtTheBoundaries()
        {
            Assert.AreEqual(2.0, S.BeaconDepth);
            Assert.AreEqual(0, S.BeaconFx);

            foreach (var ok in new[] { "beacon_depth=0.25", "beacon_depth=20", "beacon_depth=3.5", "beacon_fx=0", "beacon_fx=1", "beacon_fx=2" })
                Assert.IsTrue(MatronOverrides.TryApply(S, HazardArgs.Parse(ok), out _, out var e), ok + ": " + e);

            foreach (var bad in new[] { "beacon_depth=0", "beacon_depth=0.24", "beacon_depth=-0.1", "beacon_depth=20.5", "beacon_depth=deep", "beacon_fx=3", "beacon_fx=-1", "beacon_fx=1.5" })
            {
                Assert.IsFalse(MatronOverrides.TryApply(S, HazardArgs.Parse(bad), out var kept, out var refusal), bad);
                StringAssert.Contains(refusal, bad.Substring(0, bad.IndexOf('=')), bad);
                Assert.AreEqual(S, kept);
            }

            Assert.IsTrue(MatronOverrides.TryApply(S, HazardArgs.Parse("beacon_depth=3.5 beacon_fx=2"), out var both, out var error), error);
            Assert.AreEqual(3.5, both.BeaconDepth);
            Assert.AreEqual(2, both.BeaconFx);
        }

        [TestMethod]
        public void Beacon_FxOverride_PicksTheBeam_AndTheDepthOverrideMovesTheThreshold()
        {
            CollectionAssert.AreEqual(new[] { 0x3300079Au, 0x33000799u, 0x33000795u }, MatronLayout.DrainBeacons.ToList());

            Assert.IsTrue(MatronOverrides.TryCreateScript(S, HazardArgs.Parse("beacon_fx=1"), out var script, out var error), error);
            var (s, p) = Started(settings: script.Settings);
            ToFlowing(s, p);
            PollAt(s, p, 2.5);
            Assert.AreEqual(0x33000799u, Beacons(p).Single().Prop.Script);

            Assert.IsTrue(MatronOverrides.TryCreateScript(S, HazardArgs.Parse("beacon_depth=4"), out var deep, out error), error);
            var (s2, p2) = Started(settings: deep.Settings);
            ToFlowing(s2, p2);
            PollAt(s2, p2, 3.5);
            Assert.AreEqual(0, Beacons(p2).Count());
            PollAt(s2, p2, 4.5);
            Assert.AreEqual(1, Beacons(p2).Count());
        }

        /// <summary>Walks the call log: every fill start is preceded by a stop of the tag since the previous start.</summary>
        private static void AssertNeverTwoFills(FakePinnacleFightPort p, string what)
        {
            var live = 0;

            foreach (var c in p.Calls)
            {
                if (c.Name == nameof(IPinnacleFightPort.StartHazard) && (string)c.Args[0] == Fill)
                    live++;
                else if (c.Name == nameof(IPinnacleFightPort.StopHazard) && (string)c.Args[0] == Fill)
                    live = 0;
                else if (c.Name == nameof(IPinnacleFightPort.StopHazards))
                    live = 0;

                Assert.IsTrue(live <= 1, $"{what}: two fill runs live at once");
            }
        }
    }
}
