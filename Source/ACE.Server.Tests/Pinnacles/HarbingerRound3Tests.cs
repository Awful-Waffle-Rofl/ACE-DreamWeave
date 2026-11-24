using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Harbinger;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// Round 3 of the Harbinger (owner playtest 2026-11-19, Docs/Knots/HARBINGER-ROUND2-DESIGN.md section "Round 3"), pinned on
    /// what the RUNTIME receives through the recording fake port: the lightning ceiling beam in the floor and final runs and its
    /// reach down to the pillar, the lightning alcove's conductor wisps, the acid alcove's drips and the depth they give a pool,
    /// and Ember roll's faster, weaker balls on their linear level clamp. Reads no PropertyManager value (the script takes its
    /// settings record).
    /// </summary>
    [TestClass]
    public class HarbingerRound3Tests
    {
        private static readonly HarbingerSettings S = HarbingerSettings.Defaults;

        private static readonly HarbingerElementDef Acid = HarbingerData.Element(HarbingerElement.Acid);
        private static readonly HarbingerElementDef Lightning = HarbingerData.Element(HarbingerElement.Lightning);
        private static readonly HarbingerElementDef Fire_ = HarbingerData.Element(HarbingerElement.Fire);

        /// <summary>The beam mesh's measured length under its host (MemoryLayout.BeamOrientation).</summary>
        private const float BeamLength = 5.0f;

        // ================================================================ harness

        private static void Fire(HarbingerScript s, FakePinnacleFightPort p, string tag)
            => s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.TimerFired, Tag = tag }, p);

        private static void Ended(HarbingerScript s, FakePinnacleFightPort p, string tag, HazardOutcome outcome)
            => s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.HazardEnded, Tag = tag, Outcome = outcome }, p);

        private static void Died(HarbingerScript s, FakePinnacleFightPort p, string tag, PinnacleHandle h)
        {
            p.Kill(h);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = tag, Handle = h }, p);
        }

        private static PinnaclePoint Pt(float x, float y, float z) => new PinnaclePoint(0u, x, y, z);

        private static (HarbingerScript S, FakePinnacleFightPort P) InPhase(int phase, HarbingerSettings settings = null, int seed = 1, int level = 250)
        {
            var p = new FakePinnacleFightPort(seed) { Level = level, Phase = phase };
            var s = new HarbingerScript(settings ?? S);
            s.OnStart(p);
            s.EnterPhase(phase, p);
            return (s, p);
        }

        /// <summary>Phase of <paramref name="e"/> at its threshold, then the owner in the middle of its alcove (the arrival).</summary>
        private static (HarbingerScript S, FakePinnacleFightPort P) Arrived(HarbingerElement e, HarbingerSettings settings = null, int seed = 1, int level = 250)
        {
            var (s, p) = InPhase((int)e + 1, settings, seed, level);

            p.OwnerAt = Pt(10, -20, -36f);
            p.AtFloor.Add(p.Head);
            Fire(s, p, HarbingerScript.TimerPoll);
            p.AtFloor.Remove(p.Head);
            Assert.AreEqual(HarbingerStage.Alcove, s.Stage);

            var c = HarbingerData.AlcoveOf(e).Centre;
            p.OwnerAt = Pt(c.X, c.Y, 0f);
            Fire(s, p, HarbingerScript.TimerPoll);

            // round 4: the hazards start when the arrival grace ends; these tests are about the hazards themselves
            if (p.Timers.ContainsKey(HarbingerScript.TimerGrace))
                Fire(s, p, HarbingerScript.TimerGrace);

            return (s, p);
        }

        private static HazardRegion Resolve(FakePinnacleFightPort.HazardCall call)
        {
            var r = PinnacleHazardRegions.Resolve(call.Options, HazardRegionPresets.HarbingerName, 0, out var error);
            Assert.IsNotNull(r, error);
            return r;
        }

        private static double ScaledPct(FakePinnacleFightPort p, double authored)
            => HazardLevelScale.Scaled(p.Level, authored, S.HazardPctMin, S.HazardPctMax);

        private static double LevelScale(FakePinnacleFightPort p)
            => HazardLevelScale.BenchmarkPct(p.Level, S.HazardPctMin, S.HazardPctMax) / HazardLevelScale.AnchorPct;

        private static List<FakePinnacleFightPort.HazardCall> DripRuns(FakePinnacleFightPort p)
            => p.Hazards.Where(h => h.Tag.StartsWith(HarbingerScript.TagDripPrefix)).ToList();

        private static List<FakePinnacleFightPort.HazardCall> PoolRuns(FakePinnacleFightPort p)
            => p.Hazards.Where(h => h.Tag.StartsWith(HarbingerScript.TagPoolPrefix)).ToList();

        /// <summary>The pool slot a drip run's area is centred on.</summary>
        private static int SlotOf(FakePinnacleFightPort.HazardCall drip)
        {
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Acid);
            var a = drip.Options.Area;
            var c = new Vector2((a.MinX + a.MaxX) / 2f, (a.MinY + a.MaxY) / 2f);

            return Enumerable.Range(0, HarbingerLayout.PoolSlotsUV.Count).Single(i => Vector2.Distance(HarbingerLayout.PoolSlot(alcove, i), c) < 1e-3f);
        }

        /// <summary>The drip timer fires; the drip run it started (null when none) lands (ends Completed) when <paramref name="land"/>.</summary>
        private static FakePinnacleFightPort.HazardCall Drip(HarbingerScript s, FakePinnacleFightPort p, bool land = true)
        {
            var before = p.Hazards.Count;

            // round 4: the drip clock carries a remainder, so the fight clock reaches the drip's due time first
            if (p.Timers.TryGetValue(HarbingerScript.TimerDrip, out var due) && due > p.Now)
                p.Now = due;

            Fire(s, p, HarbingerScript.TimerDrip);
            var run = p.Hazards.Skip(before).LastOrDefault(h => h.Tag.StartsWith(HarbingerScript.TagDripPrefix));

            if (run != null && land)
                Ended(s, p, run.Tag, HazardOutcome.Completed);

            return run;
        }

        private static List<(string Tag, string Key, double Value)> ControlsOf(FakePinnacleFightPort p, string tag) => p.Controls.Where(c => c.Tag == tag).ToList();

        // ================================================================ change 2: the lightning ceiling beam

        /// <summary>
        /// Phase 2's floor run and the final's lightning run carry Grael's 1.5 s ceiling beam in the lightning def's marker look
        /// (the narrow purple beam), with a beam_height that puts the 5 m beam's foot at the chamber floor: the quantity checked
        /// is the beam's lowest z (host z from WispRules.CeilingBeamHost less the measured 5 m mesh) against the -36 floor.
        /// </summary>
        [TestMethod]
        public void LightningRuns_PhaseAndFinal_CarryTheCeilingBeam_WhoseFootReachesTheChamberFloor()
        {
            var chamber = HazardRegionPresets.Harbinger(0);

            foreach (var phase in new[] { 2, 5 })
            {
                var (_, p) = InPhase(phase);
                var run = p.Hazards.Last(h => h.Tag == Lightning.Tag);
                var ws = WispSettings.From(run.Args);

                Assert.AreEqual(HazardMechanicRegistry.Wisps, run.Kind);
                Assert.AreEqual(HarbingerSettings.LightningCeilingBeam, ws.CeilingBeam, 1e-12, $"p{phase}: Grael's 1.5 s beam");
                Assert.AreEqual(1.5, ws.CeilingBeam, 1e-12);
                Assert.AreEqual(Lightning.Visuals.MarkerScript, ws.BeamScript, $"p{phase}: the lightning marker's look");
                Assert.AreEqual(0x33000797u, ws.BeamScript, "the narrow purple beam");
                Assert.AreEqual(HarbingerSettings.LightningBeamHeight, ws.BeamHeight, 1e-12);

                var host = WispRules.CeilingBeamHost(new Vector2(20f, -20f), chamber.CeilingZ.Value, chamber.GroundZ(20f, -20f), ws.BeamHeight);
                var foot = host.Z - BeamLength;
                Assert.IsTrue(foot >= HarbingerLayout.FloorZ && foot <= HarbingerLayout.FloorZ + 0.5f, $"p{phase}: the beam's foot at z {foot}, the floor is {HarbingerLayout.FloorZ}");
                Assert.IsTrue(host.Z < chamber.CeilingZ.Value, "the host is inside the room");

                Assert.IsNull(WispRules.Validate(chamber, run.Args, run.Pattern), $"p{phase}: the run validates on the chamber");
            }

            // control: without beam_height the beam would end 19 m over the floor
            var off = WispRules.CeilingBeamHost(new Vector2(20f, -20f), chamber.CeilingZ.Value, -36f, 0.0);
            Assert.IsTrue(off.Z - BeamLength - HarbingerLayout.FloorZ > 18f);
        }

        // ================================================================ change 3: the conductor wisps

        [TestMethod]
        public void ConductorWisps_TheRuntimeReceives_TwoLightningWisps_OnTheLightningAlcove_WithTheBeam()
        {
            var (_, p) = Arrived(HarbingerElement.Lightning);
            var run = p.Hazards.Single(h => h.Tag == HarbingerScript.SignatureTag(Lightning));
            var a = run.Args;
            var ws = WispSettings.From(a);

            Assert.AreEqual("sig-lightning", run.Tag);
            Assert.AreEqual(HazardMechanicRegistry.Wisps, run.Kind);
            Assert.AreEqual(WispRules.Scatter, run.Pattern);
            Assert.AreEqual(HazardRegionPresets.HarbingerAlcoveEastName, run.Options.Region, "lightning is the east alcove");
            Assert.IsNull(run.Options.Area);
            Assert.IsNull(run.Options.HitSlow);

            Assert.AreEqual(2, ws.Count);
            Assert.AreEqual(1.0f, ws.PillarRadius);
            Assert.AreEqual("electric", a.GetString("damage_type"));
            Assert.AreEqual(ScaledPct(p, S.ConductorWispPct), a.GetDouble("pct", 0, 0, 9), 1e-12, "the authored 0.20 through the level scale");
            Assert.AreEqual(Lightning.Visuals.WispWcid, ws.WispWcid, "the Storm Wisp");
            Assert.AreEqual(Lightning.Visuals.PillarScript, ws.Script);
            Assert.AreEqual(1.5, ws.CeilingBeam, 1e-12);
            Assert.AreEqual(Lightning.Visuals.MarkerScript, ws.BeamScript);

            var region = Resolve(run);
            var host = WispRules.CeilingBeamHost(new Vector2(region.CenterX, region.CenterY), region.CeilingZ.Value, region.GroundZ(region.CenterX, region.CenterY), ws.BeamHeight);
            var foot = host.Z - BeamLength;
            Assert.IsTrue(foot >= HarbingerLayout.AlcoveZ && foot <= HarbingerLayout.AlcoveZ + 0.5f, $"the beam's foot at z {foot} on the alcove floor");

            Assert.IsNull(WispRules.Validate(region, a, run.Pattern), "the wisps validate on the alcove");
        }

        [TestMethod]
        public void ConductorWisps_ValidateAtEveryCountBound_PressedOrNot_AndAtTheLowestLevel()
        {
            foreach (var n in new[] { 1, 2, 3 })
            {
                foreach (var pressed in new[] { false, true })
                {
                    var settings = S with { ConductorWisps = n, ConductorPressureWisps = n };
                    var (s, p) = Arrived(HarbingerElement.Lightning, settings, level: 1);

                    if (pressed)
                        Fire(s, p, HarbingerScript.TimerPressure);

                    var run = p.Hazards.Last(h => h.Tag == HarbingerScript.SignatureTag(Lightning));
                    Assert.AreEqual(n, run.Args.GetInt("n", 0, 0, 99));
                    Assert.IsNull(WispRules.Validate(Resolve(run), run.Args, run.Pattern), $"n={n} pressed={pressed}");
                }
            }
        }

        [TestMethod]
        public void ConductorWisps_AreAWantedRun_RestartedOnCompletion_AndStoppedAtTheCrystalsDeath()
        {
            var (s, p) = Arrived(HarbingerElement.Lightning);
            var tag = HarbingerScript.SignatureTag(Lightning);

            Ended(s, p, tag, HazardOutcome.Completed);
            Assert.AreEqual(2, p.Hazards.Count(h => h.Tag == tag), "a Completed run restarts");
            Assert.AreEqual(p.Hazards.First(h => h.Tag == tag).Args.ToString(), p.Hazards.Last(h => h.Tag == tag).Args.ToString());

            var guards = p.Spawns.Where(x => x.Tag == Lightning.GuardTag).Select(x => x.Result).TakeLast(2).ToArray();
            Died(s, p, Lightning.GuardTag, guards[0]);
            Died(s, p, Lightning.GuardTag, guards[1]);
            var crystal = p.Spawns.Last(x => x.Tag == Lightning.CrystalTag).Result;

            p.ClearRecordings();
            Died(s, p, Lightning.CrystalTag, crystal);
            CollectionAssert.Contains(p.StoppedHazardTags, tag);

            Ended(s, p, tag, HazardOutcome.Completed);
            Assert.AreEqual(0, p.Hazards.Count(h => h.Tag == tag), "no longer wanted");
        }

        // ================================================================ change 4: the acid drips

        [TestMethod]
        public void Drip_TheRuntimeReceives_OneSmallGreenBall_FallingOnItsSlot()
        {
            var (s, p) = Arrived(HarbingerElement.Acid);
            Assert.AreEqual(S.DripSeconds, p.TimerSets.Last(t => t.Tag == HarbingerScript.TimerDrip).Seconds);

            var run = Drip(s, p, land: false);
            Assert.IsNotNull(run, "the timer dripped");
            var a = run.Args;
            var rs = RainSettings.From(a);
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Acid);

            Assert.AreEqual(HarbingerScript.TagDripPrefix + "1", run.Tag);
            Assert.AreEqual(HazardMechanicRegistry.Rain, run.Kind);
            Assert.IsNull(run.Pattern);
            Assert.IsNull(run.Options.Region);
            Assert.AreEqual(HarbingerLayout.PoolArea(alcove, SlotOf(run), HarbingerSettings.DripAreaSize), run.Options.Area, "a small area round one pool slot");

            Assert.AreEqual(HarbingerData.AcidDripWcid, rs.BallWcid, "the small green drip ball");
            Assert.AreEqual(1008281u, rs.BallWcid);
            Assert.AreEqual(0.3, rs.BallRadius, 1e-12);
            Assert.AreEqual(1, rs.MaxFalling);
            Assert.AreEqual(0.0, rs.TargetShare, "it lands where the script chose");
            Assert.AreEqual(0f, rs.EdgeInset);
            Assert.AreEqual(Acid.Visuals.MarkerScript, rs.MarkerScript, "the green ground marker");
            Assert.AreEqual(Acid.Visuals.BurstScript, rs.BurstScript);
            Assert.AreEqual("acid", a.GetString("damage_type"));
            Assert.AreEqual(0.04, S.DripPct, "the Whiteout hail's authored value (orchestrator call on review)");
            Assert.AreEqual(ScaledPct(p, S.DripPct), a.GetDouble("pct", 0, 0, 9), 1e-12, "the authored 0.04 through the level scale");

            var region = Resolve(run);
            Assert.IsNull(RainRules.Validate(region, a, null), "the drip validates on its area");
            Assert.IsTrue(rs.DropZ.Value + rs.BallRadius <= HazardRegionPresets.HarbingerAlcoveCeilingZ - 0.5, "near the ceiling, under it");

            // ONE drop: a ball's lifetime outlasts the launch window, and the cap is one ball (RainDirector launches at the start)
            Assert.IsTrue(RainRules.NominalLifetime(region, rs) > rs.Duration + 0.25, $"lifetime {RainRules.NominalLifetime(region, rs)} vs window {rs.Duration}");

            // in the air, the drip has started no pool yet
            Assert.AreEqual(0, PoolRuns(p).Count);
        }

        [TestMethod]
        public void Drip_Landing_StartsAPool_ThenEachLandingDeepensIt_ToTheMaxDepth_AndARestartKeepsTheDepth()
        {
            // a cap of one pool: the first drip starts it, every later one aims at it
            var (s, p) = Arrived(HarbingerElement.Acid, S with { PoolCap = 1 });

            var first = Drip(s, p);
            var slot = SlotOf(first);
            var pool = PoolRuns(p).Single();
            Assert.AreEqual(HarbingerScript.PoolTag(slot), pool.Tag, "the pool starts where the drop landed");
            Assert.AreEqual(HarbingerSettings.PoolSurfaceZ, pool.Args.GetDouble("start_z", 0, -9, 9), 1e-12);

            var expected = new[] { 0.35, 0.5, 0.6 };

            foreach (var z in expected)
            {
                p.Controls.Clear();
                var drip = Drip(s, p);
                Assert.AreEqual(slot, SlotOf(drip), "at the cap the drip aims at the pool");
                CollectionAssert.AreEqual(new List<(string, string, double)> { (pool.Tag, "rate", HarbingerSettings.PoolRiseRate), (pool.Tag, "target", z) },
                    ControlsOf(p, pool.Tag).Select(c => (c.Tag, c.Key, Math.Round(c.Value, 9))).ToList(), $"the rise to {z}");
                Assert.AreEqual(1, PoolRuns(p).Count, "a deepening drip starts no new pool");
            }

            // at its deepest a drip still falls (it hurts) but raises nothing
            p.Controls.Clear();
            Assert.IsNotNull(Drip(s, p));
            Assert.AreEqual(0, ControlsOf(p, pool.Tag).Count);

            // a restart of the pool's run starts from the depth the drips gave it, and its hit grows with the depth
            Ended(s, p, pool.Tag, HazardOutcome.Aborted);
            var restarted = PoolRuns(p).Last();
            Assert.AreEqual(2, PoolRuns(p).Count);
            Assert.AreEqual(S.PoolMaxDepth, restarted.Args.GetDouble("start_z", 0, -9, 9), 1e-12);

            var region = Resolve(restarted);
            var fresh = FillSettings.From(pool.Args, region);
            var deep = FillSettings.From(restarted.Args, region);
            var shallowHit = FillRules.FlatDamage(fresh.StartZ - fresh.GroundZ, fresh, fresh.DamageScale);
            var deepHit = FillRules.FlatDamage(deep.StartZ - deep.GroundZ, deep, deep.DamageScale);

            Assert.AreEqual((30 + 100 * 0.2) * LevelScale(p), shallowHit, 1e-9, "a fresh pool: round 2's 50 at level scale");
            Assert.AreEqual((30 + 100 * 0.6) * LevelScale(p), deepHit, 1e-9, "the deepest pool: 90");
            Assert.IsTrue(deepHit > shallowHit * 1.7);
            Assert.IsNull(FillRules.Validate(region, restarted.Args), "the deep pool validates");
        }

        [TestMethod]
        public void Drip_ThatNeverLanded_StartsAndRaisesNothing()
        {
            var (s, p) = Arrived(HarbingerElement.Acid);

            foreach (var outcome in new[] { HazardOutcome.Aborted, HazardOutcome.Stopped, HazardOutcome.Failed })
            {
                var drip = Drip(s, p, land: false);
                Ended(s, p, drip.Tag, outcome);
                Assert.AreEqual(0, PoolRuns(p).Count, outcome.ToString());
                Assert.AreEqual(0, p.Controls.Count, outcome.ToString());
            }

            // and an end delivered twice acts once
            var landed = Drip(s, p);
            Ended(s, p, landed.Tag, HazardOutcome.Completed);
            Assert.AreEqual(1, PoolRuns(p).Count);
        }

        [TestMethod]
        public void Drip_LandingOnASlotThatIsNoLongerFree_AtTheCap_DeepensTheNearestPoolInstead()
        {
            var (s, p) = Arrived(HarbingerElement.Acid, S with { PoolCap = 1 });
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Acid);

            // the drip is aimed at a free slot (no pool yet)...
            var drip = Drip(s, p, land: false);
            var aimed = SlotOf(drip);

            // ...and before it lands a guardian dies on the slot farthest from it: its pool takes the only place under the cap
            var far = Enumerable.Range(0, HarbingerLayout.PoolSlotsUV.Count)
                .OrderByDescending(i => Vector2.Distance(HarbingerLayout.PoolSlot(alcove, i), HarbingerLayout.PoolSlot(alcove, aimed))).First();
            var at = HarbingerLayout.PoolSlot(alcove, far);
            var guard = p.Spawns.Where(x => x.Tag == Acid.GuardTag).Select(x => x.Result).Last();
            p.PositionOf[guard] = Pt(at.X, at.Y, 0f);
            Fire(s, p, HarbingerScript.TimerPoll);
            Died(s, p, Acid.GuardTag, guard);
            Assert.AreEqual(HarbingerScript.PoolTag(far), PoolRuns(p).Single().Tag);

            p.Controls.Clear();
            Ended(s, p, drip.Tag, HazardOutcome.Completed);

            Assert.AreEqual(1, PoolRuns(p).Count, "the cap holds: no pool where the drop landed");
            Assert.AreEqual(0.35, ControlsOf(p, HarbingerScript.PoolTag(far)).Single(c => c.Key == "target").Value, 1e-9, "the nearest (only) pool deepens");
        }

        [TestMethod]
        public void Drips_SometimesDeepenAPool_SometimesStartANewOne_DrawnWithTheFightsRng()
        {
            int deepened = 0, started = 0;

            foreach (var seed in Enumerable.Range(1, 40))
            {
                var (s, p) = Arrived(HarbingerElement.Acid, seed: seed);
                Drip(s, p);
                Assert.AreEqual(1, PoolRuns(p).Count, $"seed {seed}: the first drip starts a pool");

                p.Controls.Clear();
                Drip(s, p);

                if (PoolRuns(p).Count == 2)
                    started++;
                else if (p.Controls.Any(c => c.Key == "target"))
                    deepened++;
                else
                    Assert.Fail($"seed {seed}: the second drip did neither");
            }

            Assert.IsTrue(deepened >= 8 && started >= 8, $"about half each: deepened {deepened}, started {started}");
        }

        [TestMethod]
        public void DripSlots_EveryCentreStandsOnTheMeasuredFloor_InEveryAlcove()
        {
            // dat-free pin (review F2): the measured floor and cell column reach 4.25 m from the centre; the dat-backed
            // proof of a cell at drop height over every drip area is HarbingerAlcoveRegionTests (RequiresDatFiles)
            foreach (var alcove in HarbingerData.Alcoves)
                for (var i = 0; i < HarbingerLayout.PoolSlotsUV.Count; i++)
                    Assert.IsTrue(Vector2.Distance(HarbingerLayout.PoolSlot(alcove, i), alcove.Centre) <= 4.25f, $"{alcove.Name} slot {i}");
        }

        // ================================================================ review F1: overrides that would make a signature unstartable

        [TestMethod]
        public void Overrides_ThatMakeEmberRollUnstartable_AreRefused_WithTheBallMechanicsMessage()
        {
            foreach (var overrides in new[] { "ember_pressure_speed=3 ember_speed=1", "ember_count=3 ember_every_seconds=2 ember_speed=1" })
            {
                Assert.IsFalse(HarbingerOverrides.TryApply(S, HazardArgs.Parse(overrides), out var result, out var error), overrides);
                StringAssert.Contains(error, "random launch rate", overrides);
                StringAssert.Contains(error, "Ember roll", overrides);
                Assert.AreSame(S, result, "the base, unchanged");
                Assert.IsFalse(HarbingerOverrides.TryCreateScript(S, HazardArgs.Parse(overrides), out var script, out _), overrides);
                Assert.IsNull(script);
            }

            // control: the same mechanic refuses those args directly
            Assert.IsNotNull(BallRules.Validate(HarbingerSignatureReach.Preset(HarbingerElement.Fire), (S with { EmberSpeed = 1 }).EmberArgs(0.05, false), BallPatterns.Random));
        }

        [TestMethod]
        public void Defaults_AndEveryShippedSignatureRun_PassThePreflight()
        {
            Assert.IsNull(HarbingerOverrides.SignatureProblem(S));
            Assert.IsTrue(HarbingerOverrides.TryApply(S, HazardArgs.Empty, out _, out var error), error);
            Assert.IsTrue(HarbingerOverrides.TryApply(S, HazardArgs.Parse("pressure_seconds=30"), out _, out error), error);
            Assert.IsTrue(HarbingerOverrides.TryApply(S, HazardArgs.Parse("ember_count=3 ember_pressure_count=3"), out _, out error), error);
        }

        // ================================================================ change 5: Ember roll

        [TestMethod]
        public void EmberPctAt_IsTheLinearThreeToTenPercentClamp()
        {
            var cases = new (int Level, double Pct)[] { (1, 0.03), (150, 0.03), (200, 0.03), (250, 0.0475), (300, 0.065), (350, 0.0825), (400, 0.10), (600, 0.10) };

            foreach (var (level, pct) in cases)
                Assert.AreEqual(pct, S.EmberPctAt(level), 1e-12, $"level {level}");
        }

        [TestMethod]
        public void EmberRoll_TheRuntimeReceives_TheLinearPct_AndTheFasterDenserBalls()
        {
            foreach (var (level, pct) in new (int, double)[] { (150, 0.03), (300, 0.065), (500, 0.10) })
            {
                var (s, p) = Arrived(HarbingerElement.Fire, level: level);
                var run = p.Hazards.Last(h => h.Tag == HarbingerScript.SignatureTag(Fire_));

                Assert.AreEqual(pct, run.Args.GetDouble("pct", 0, 0, 9), 1e-12, $"level {level}");
                Assert.AreEqual(2, run.Args.GetInt("count", 0, 0, 99));
                Assert.AreEqual(3.0, run.Args.GetDouble("wave_every", 0, 0, 99), 1e-12);
                Assert.AreEqual(4.0, run.Args.GetDouble("speed", 0, 0, 99), 1e-12);
                Assert.AreEqual(3.0, run.Args.GetDouble("window", 0, 0, 99), 1e-12, "the window is the wave period");
                Assert.AreEqual(0.5, run.Args.GetDouble("ball_radius", 0, 0, 9), 1e-12);

                Fire(s, p, HarbingerScript.TimerPressure);
                var pressed = p.Hazards.Last(h => h.Tag == HarbingerScript.SignatureTag(Fire_));

                Assert.AreEqual(pct, pressed.Args.GetDouble("pct", 0, 0, 9), 1e-12, $"level {level} pressed");
                Assert.AreEqual(3, pressed.Args.GetInt("count", 0, 0, 99));
                Assert.AreEqual(2.0, pressed.Args.GetDouble("wave_every", 0, 0, 99), 1e-12);
                Assert.AreEqual(5.0, pressed.Args.GetDouble("speed", 0, 0, 99), 1e-12);
                Assert.AreEqual(2.0, pressed.Args.GetDouble("window", 0, 0, 99), 1e-12);
            }
        }

        [TestMethod]
        public void EmberRoll_Validates_AtEveryCountBound_PressedOrNot()
        {
            foreach (var count in new[] { 1, 2, 3 })
            {
                var settings = S with { EmberCount = count, EmberPressureCount = count };

                foreach (var pressed in new[] { false, true })
                {
                    var (s, p) = Arrived(HarbingerElement.Fire, settings);

                    if (pressed)
                        Fire(s, p, HarbingerScript.TimerPressure);

                    var run = p.Hazards.Last(h => h.Tag == HarbingerScript.SignatureTag(Fire_));
                    Assert.AreEqual(count, run.Args.GetInt("count", 0, 0, 99));
                    Assert.IsNull(BallRules.Validate(Resolve(run), run.Args, run.Pattern), $"count {count} pressed {pressed}: {run.Args}");
                }
            }
        }
    }
}
