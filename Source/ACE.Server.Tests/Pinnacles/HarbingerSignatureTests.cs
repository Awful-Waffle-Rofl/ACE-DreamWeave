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
    /// The alcove signatures and the doorway pressure (round 2 B1, B2) against the recording fake port: what each signature
    /// hands the runtime (kind, pattern, region or area, args, hit slow), when it starts and stops, the Twin conductors revive
    /// against the crystal's ward, and the reach of every signature against the measured alcove floor (a model here; the real
    /// floor in HarbingerAlcoveRegionTests). The port applies nothing: a test delivers every event itself. Reads no
    /// PropertyManager value (the script takes its settings record).
    /// </summary>
    [TestClass]
    public class HarbingerSignatureTests
    {
        private static readonly HarbingerSettings S = HarbingerSettings.Defaults;

        // ================================================================ harness

        private static (HarbingerScript S, FakePinnacleFightPort P) Started(HarbingerSettings settings = null, int seed = 1)
        {
            var p = new FakePinnacleFightPort(seed);
            var s = new HarbingerScript(settings ?? S);
            s.OnStart(p);
            s.EnterPhase(1, p);
            return (s, p);
        }

        private static void Fire(HarbingerScript s, FakePinnacleFightPort p, string tag)
            => s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.TimerFired, Tag = tag }, p);

        private static void Died(HarbingerScript s, FakePinnacleFightPort p, string tag, PinnacleHandle h)
        {
            p.Kill(h);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = tag, Handle = h }, p);
        }

        private static void Ended(HarbingerScript s, FakePinnacleFightPort p, string tag, HazardOutcome outcome)
            => s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.HazardEnded, Tag = tag, Outcome = outcome }, p);

        private static PinnaclePoint Pt(float x, float y, float z) => new PinnaclePoint(0u, x, y, z);

        /// <summary>
        /// The drip timer fires (round 3); when it started a drip run and <paramref name="land"/>, that run ends Completed (its
        /// impact). Returns the drip's tag, or null when no drip fell.
        /// </summary>
        private static string Drip(HarbingerScript s, FakePinnacleFightPort p, bool land = true)
        {
            var before = p.Hazards.Count;

            // round 4: the drip clock carries a remainder, so the fight clock reaches the drip's due time first (one drip owed)
            if (p.Timers.TryGetValue(HarbingerScript.TimerDrip, out var due) && due > p.Now)
                p.Now = due;

            Fire(s, p, HarbingerScript.TimerDrip);
            var run = p.Hazards.Skip(before).LastOrDefault(h => h.Tag.StartsWith(HarbingerScript.TagDripPrefix));

            if (run != null && land)
                Ended(s, p, run.Tag, HazardOutcome.Completed);

            return run?.Tag;
        }

        /// <summary>The live pool runs' slots, in start order (the last run of each pool tag that was never stopped).</summary>
        private static List<int> PoolSlots(FakePinnacleFightPort p)
            => p.Hazards.Where(h => h.Tag.StartsWith(HarbingerScript.TagPoolPrefix)).Select(h => int.Parse(h.Tag.Substring(HarbingerScript.TagPoolPrefix.Length))).Distinct().ToList();

        /// <summary>The phase of <paramref name="e"/> at its threshold: the alcove is up, the owner still on the floor.</summary>
        private static void ToAlcove(HarbingerScript s, FakePinnacleFightPort p, HarbingerElement e)
        {
            if (p.Phase != (int)e + 1)
            {
                p.Phase = (int)e + 1;
                s.EnterPhase(p.Phase, p);
            }

            p.OwnerAt = Pt(10, -20, -36f);
            p.AtFloor.Add(p.Head);
            Fire(s, p, HarbingerScript.TimerPoll);
            p.AtFloor.Remove(p.Head);
        }

        /// <summary>The owner stands in the middle of <paramref name="e"/>'s alcove, and the poll sees the arrival.</summary>
        private static void Arrive(HarbingerScript s, FakePinnacleFightPort p, HarbingerElement e)
        {
            ArriveRaw(s, p, e);

            // round 4: the hazards start when the arrival grace ends. Every older test is about the hazards themselves, so it
            // lets the grace run out here; the grace tests use ArriveRaw
            if (p.Timers.ContainsKey(HarbingerScript.TimerGrace))
                Fire(s, p, HarbingerScript.TimerGrace);
        }

        /// <summary>The arrival alone: the grace timer is armed and nothing it guards has started.</summary>
        private static void ArriveRaw(HarbingerScript s, FakePinnacleFightPort p, HarbingerElement e)
        {
            var c = HarbingerData.AlcoveOf(e).Centre;
            p.OwnerAt = Pt(c.X, c.Y, 0f);
            Fire(s, p, HarbingerScript.TimerPoll);
        }

        private static (HarbingerScript S, FakePinnacleFightPort P) Arrived(HarbingerElement e, HarbingerSettings settings = null, int seed = 1)
        {
            var (s, p) = Started(settings, seed);
            ToAlcove(s, p, e);
            Arrive(s, p, e);
            return (s, p);
        }

        private static PinnacleHandle Crystal(FakePinnacleFightPort p, HarbingerElementDef def) => p.Spawns.Last(x => x.Tag == def.CrystalTag).Result;

        private static PinnacleHandle[] Guards(FakePinnacleFightPort p, HarbingerElementDef def) => p.Spawns.Where(x => x.Tag == def.GuardTag).Select(x => x.Result).TakeLast(2).ToArray();

        private static bool IsSig(string tag) => tag.StartsWith(HarbingerScript.TagSignaturePrefix) || tag.StartsWith(HarbingerScript.TagPoolPrefix);

        /// <summary>The signature runs other than the acid film (round 4): the pools, wisps, Whiteout and Ember roll. <see cref="FilmRuns"/> are the film's.</summary>
        private static List<FakePinnacleFightPort.HazardCall> SigRuns(FakePinnacleFightPort p) => p.Hazards.Where(h => IsSig(h.Tag) && h.Tag != HarbingerScript.TagFilm).ToList();

        private static List<FakePinnacleFightPort.HazardCall> FilmRuns(FakePinnacleFightPort p) => p.Hazards.Where(h => h.Tag == HarbingerScript.TagFilm).ToList();

        private static bool Damageable(FakePinnacleFightPort p, PinnacleHandle h)
            => !p.Immune.GetValueOrDefault(h, false) && p.Floors.GetValueOrDefault(h, null) == null;

        private static double LastTimer(FakePinnacleFightPort p, string tag) => p.TimerSets.Last(t => t.Tag == tag).Seconds;

        private static HazardRegion Resolve(FakePinnacleFightPort.HazardCall call)
        {
            var r = PinnacleHazardRegions.Resolve(call.Options, HazardRegionPresets.HarbingerName, 0, out var error);
            Assert.IsNotNull(r, error);
            return r;
        }

        private static readonly HarbingerElementDef Acid = HarbingerData.Element(HarbingerElement.Acid);
        private static readonly HarbingerElementDef Lightning = HarbingerData.Element(HarbingerElement.Lightning);
        private static readonly HarbingerElementDef Frost = HarbingerData.Element(HarbingerElement.Frost);
        private static readonly HarbingerElementDef Fire_ = HarbingerData.Element(HarbingerElement.Fire);

        // ================================================================ lifecycle

        [TestMethod]
        public void Signature_NeverStartsAtTheThreshold_StartsOnArrival_Once()
        {
            foreach (var def in HarbingerData.Elements)
            {
                var (s, p) = Started();
                ToAlcove(s, p, def.Element);

                Assert.AreEqual(0, SigRuns(p).Count, $"{def.Tag}: no signature run at the threshold");
                Assert.IsFalse(p.Timers.ContainsKey(HarbingerScript.TimerPressure), $"{def.Tag}: no pressure before the arrival");
                Assert.IsFalse(p.Timers.ContainsKey(HarbingerScript.TimerDrip), def.Tag);

                Arrive(s, p, def.Element);
                Assert.AreEqual(S.PressureSeconds, LastTimer(p, HarbingerScript.TimerPressure), def.Tag);

                var runs = SigRuns(p).Count;
                var expected = def.Element == HarbingerElement.Acid ? 0 : 1;   // round 3: the lightning alcove's conductor wisps
                Assert.AreEqual(expected, runs, $"{def.Tag}: signature runs at the arrival");

                // later polls with the owner still inside never start it again
                Fire(s, p, HarbingerScript.TimerPoll);
                Fire(s, p, HarbingerScript.TimerPoll);
                Assert.AreEqual(expected, SigRuns(p).Count, $"{def.Tag}: no double start");
                Assert.AreEqual(1, p.TimerSets.Count(t => t.Tag == HarbingerScript.TimerPressure), $"{def.Tag}: the pressure is armed once");
                StringAssert.Contains(s.Describe(), "sig=on");
            }
        }

        [TestMethod]
        public void Acid_StartsDripping_Lightning_StartsItsWispsButNoReviveUntilAGuardDies()
        {
            var (_, a) = Arrived(HarbingerElement.Acid);
            Assert.AreEqual(S.DripSeconds, LastTimer(a, HarbingerScript.TimerDrip));
            Assert.AreEqual(0, a.Hazards.Count(h => h.Tag.StartsWith(HarbingerScript.TagDripPrefix)), "the first drip falls when the timer fires");

            var (_, l) = Arrived(HarbingerElement.Lightning);
            Assert.IsFalse(l.TimerSets.Any(t => t.Tag == HarbingerScript.TimerDrip));
            Assert.IsFalse(l.TimerSets.Any(t => t.Tag.StartsWith(HarbingerScript.TimerRevivePrefix)));
            Assert.AreEqual(HarbingerScript.SignatureTag(Lightning), SigRuns(l).Single().Tag);
        }

        [TestMethod]
        public void CrystalDeath_StopsTheSignature_ItsRunsTimersAndPools()
        {
            foreach (var def in HarbingerData.Elements)
            {
                var (s, p) = Arrived(def.Element);

                string inAir = null;

                if (def.Element == HarbingerElement.Acid)
                {
                    Drip(s, p);
                    Drip(s, p);
                    inAir = Drip(s, p, land: false);
                    Assert.IsNotNull(inAir, "a drip in the air at the crystal's death");
                }

                var started = SigRuns(p).Select(h => h.Tag).Distinct().ToList();
                Assert.IsTrue(def.Element == HarbingerElement.Lightning || started.Count > 0, def.Tag);

                var guards = Guards(p, def);
                Died(s, p, def.GuardTag, guards[0]);
                Died(s, p, def.GuardTag, guards[1]);
                var crystal = Crystal(p, def);

                p.ClearRecordings();
                Died(s, p, def.CrystalTag, crystal);

                foreach (var tag in started)
                    CollectionAssert.Contains(p.StoppedHazardTags, tag, $"{def.Tag}: {tag} is stopped at the crystal's death");

                if (inAir != null)
                    CollectionAssert.Contains(p.StoppedHazardTags, inAir, "the drip in the air is stopped too");

                foreach (var t in new[] { HarbingerScript.TimerDrip, HarbingerScript.TimerPressure, HarbingerScript.TimerRevivePrefix + 0, HarbingerScript.TimerRevivePrefix + 1 })
                    CollectionAssert.Contains(p.CancelledTimers, t, $"{def.Tag}: {t} cancelled");

                StringAssert.Contains(s.Describe(), "sig=off");
                StringAssert.Contains(s.Describe(), "pools=0");

                // the stopped runs are no longer wanted: a late HazardEnded and the stale timers restart nothing
                p.ClearRecordings();

                foreach (var tag in started)
                    Ended(s, p, tag, HazardOutcome.Completed);

                if (inAir != null)
                    Ended(s, p, inAir, HazardOutcome.Completed);   // its impact, late: no pool

                Fire(s, p, HarbingerScript.TimerDrip);
                Fire(s, p, HarbingerScript.TimerPressure);
                Fire(s, p, HarbingerScript.TimerRevivePrefix + 0);

                Assert.AreEqual(0, p.Hazards.Count, def.Tag);
                Assert.AreEqual(0, p.Spawns.Count, def.Tag);
                CollectionAssert.DoesNotContain(p.Said, HarbingerLines.LinePressure, def.Tag);
            }
        }

        [TestMethod]
        public void Resume_StopsTheSignature_AndOnlyTheNextArrivalStartsItAgain()
        {
            foreach (var def in new[] { Frost, Fire_, Acid, Lightning })
            {
                var (s, p) = Arrived(def.Element);
                string inAir = null;

                if (def.Element == HarbingerElement.Acid)
                {
                    Drip(s, p);
                    inAir = Drip(s, p, land: false);   // review F4: a drip still falling at the death
                    Assert.IsNotNull(inAir);
                }

                var started = SigRuns(p).Select(h => h.Tag).Distinct().ToList();
                Assert.IsTrue(started.Count > 0, def.Tag);

                // a hold (the core stopped every run) and the resume
                p.ClearRecordings();
                s.RestartPhase(p);

                foreach (var tag in started)
                    CollectionAssert.Contains(p.StoppedHazardTags, tag, def.Tag);

                if (inAir != null)
                {
                    CollectionAssert.Contains(p.StoppedHazardTags, inAir, "the drip in the air is stopped at the resume");

                    // its impact, delivered late: no pool starts and none deepens
                    Ended(s, p, inAir, HazardOutcome.Completed);
                    Assert.AreEqual(0, p.Hazards.Count(h => h.Tag.StartsWith(HarbingerScript.TagPoolPrefix)), "a late drip impact starts no pool");
                    Assert.AreEqual(0, p.Controls.Count, "and raises none");
                }

                Assert.AreEqual(0, SigRuns(p).Count, $"{def.Tag}: the resume starts no signature run");
                CollectionAssert.Contains(p.CancelledTimers, HarbingerScript.TimerPressure, def.Tag);
                StringAssert.Contains(s.Describe(), "sig=off");

                // a queued HazardEnded of an old signature run is not wanted any more
                foreach (var tag in started)
                    Ended(s, p, tag, HazardOutcome.Aborted);

                Assert.AreEqual(0, SigRuns(p).Count, def.Tag);

                // the next arrival (the owner was down: arrivalSaid was reset) starts it once more
                Arrive(s, p, def.Element);

                if (def.Element == HarbingerElement.Acid)
                {
                    Assert.AreEqual(S.DripSeconds, LastTimer(p, HarbingerScript.TimerDrip), def.Tag);
                }
                else
                {
                    Assert.AreEqual(1, SigRuns(p).Count, def.Tag);
                    Assert.AreEqual(HarbingerScript.SignatureTag(def), SigRuns(p).Single().Tag);
                }
            }
        }

        [TestMethod]
        public void Final_NeverRunsASignature()
        {
            var (s, p) = Started();
            p.Phase = 5;
            s.EnterPhase(5, p);
            p.OwnerAt = Pt(0, -20, 0f);   // even standing in an alcove

            for (var i = 0; i < 5; i++)
                Fire(s, p, HarbingerScript.TimerPoll);

            Fire(s, p, HarbingerScript.TimerDrip);
            Fire(s, p, HarbingerScript.TimerPressure);
            Fire(s, p, HarbingerScript.TimerRevivePrefix + 0);

            Assert.AreEqual(0, SigRuns(p).Count);
            Assert.AreEqual(0, p.Hazards.Count(h => h.Tag.StartsWith(HarbingerScript.TagDripPrefix)), "no drip");
            Assert.IsFalse(p.TimerSets.Any(t => t.Tag == HarbingerScript.TimerPressure || t.Tag == HarbingerScript.TimerDrip));
            CollectionAssert.DoesNotContain(p.Said, HarbingerLines.LinePressure);
            CollectionAssert.DoesNotContain(p.Said, HarbingerLines.LineTwinRevive);
        }

        [TestMethod]
        public void SignatureRun_EndedIsRestartedThroughWanted_AndAbandonedAfterTheRestartCap()
        {
            var (s, p) = Arrived(HarbingerElement.Fire);
            var tag = HarbingerScript.SignatureTag(Fire_);
            var first = SigRuns(p).Single();

            // a Completed run always restarts, with the same args and region
            Ended(s, p, tag, HazardOutcome.Completed);
            Assert.AreEqual(2, SigRuns(p).Count);
            Assert.AreEqual(first.Args.ToString(), SigRuns(p).Last().Args.ToString());
            Assert.AreEqual(first.Options, SigRuns(p).Last().Options);

            for (var i = 0; i < HarbingerScript.MaxRunRestarts; i++)
                Ended(s, p, tag, HazardOutcome.Aborted);

            Assert.AreEqual(2 + HarbingerScript.MaxRunRestarts, SigRuns(p).Count);

            Ended(s, p, tag, HazardOutcome.Aborted);
            Assert.AreEqual(2 + HarbingerScript.MaxRunRestarts, SigRuns(p).Count, "abandoned after the cap");
            Assert.IsFalse(s.Describe().Contains(tag), "no longer wanted");
        }

        // ================================================================ doorway pressure

        [TestMethod]
        public void Pressure_FiresOnce_SaysTheLine_AndPressesEachSignature()
        {
            foreach (var def in HarbingerData.Elements)
            {
                var (s, p) = Arrived(def.Element);
                var before = SigRuns(p).LastOrDefault();

                p.ClearRecordings();
                Fire(s, p, HarbingerScript.TimerPressure);

                Assert.AreEqual(1, p.Said.Count(l => l == HarbingerLines.LinePressure), def.Tag);
                StringAssert.Contains(s.Describe(), "sig=pressed");

                switch (def.Element)
                {
                    case HarbingerElement.Acid:
                        Assert.AreEqual(S.DripPressureSeconds, LastTimer(p, HarbingerScript.TimerDrip), "faster drips");
                        break;

                    case HarbingerElement.Lightning:
                    {
                        // round 3: the conductor wisps restart pressed (the revive halving is pinned below)
                        CollectionAssert.Contains(p.StoppedHazardTags, HarbingerScript.SignatureTag(def));
                        var after = SigRuns(p).Single();
                        Assert.AreEqual(S.ConductorPressureWisps, after.Args.GetInt("n", 0, 0, 99));
                        Assert.AreEqual(HarbingerSettings.ConductorPressureRest, after.Args.GetDouble("rest", 0, 0, 99), 1e-9);
                        Assert.AreEqual(S.ConductorWisps, before.Args.GetInt("n", 0, 0, 99));
                        Assert.AreEqual(HarbingerSettings.ConductorRest, before.Args.GetDouble("rest", 0, 0, 99), 1e-9);
                        Assert.IsTrue(S.ConductorPressureWisps > S.ConductorWisps, "more wisps");
                        Assert.AreEqual(before.Options, after.Options, "same region");
                        break;
                    }

                    case HarbingerElement.Frost:
                    {
                        CollectionAssert.Contains(p.StoppedHazardTags, HarbingerScript.SignatureTag(def));
                        var after = SigRuns(p).Single();
                        Assert.AreEqual(S.WhiteoutPressureMaxFalling, after.Args.GetInt("max_falling", 0, 0, 99));
                        Assert.AreEqual(S.WhiteoutPressureSpeed, after.Args.GetDouble("speed", 0, 0, 99), 1e-9);
                        Assert.AreEqual(S.WhiteoutMaxFalling, before.Args.GetInt("max_falling", 0, 0, 99));
                        Assert.AreEqual(S.WhiteoutSpeed, before.Args.GetDouble("speed", 0, 0, 99), 1e-9);
                        Assert.IsTrue(S.WhiteoutPressureMaxFalling > S.WhiteoutMaxFalling && S.WhiteoutPressureSpeed > S.WhiteoutSpeed, "denser and faster");
                        Assert.AreEqual(before.Options, after.Options, "same region, same slow");
                        break;
                    }

                    case HarbingerElement.Fire:
                    {
                        CollectionAssert.Contains(p.StoppedHazardTags, HarbingerScript.SignatureTag(def));
                        var after = SigRuns(p).Single();
                        Assert.AreEqual(S.EmberPressureEverySeconds, after.Args.GetDouble("wave_every", 0, 0, 999), 1e-9);
                        Assert.AreEqual(S.EmberPressureSpeed, after.Args.GetDouble("speed", 0, 0, 99), 1e-9);
                        Assert.AreEqual(S.EmberEverySeconds, before.Args.GetDouble("wave_every", 0, 0, 999), 1e-9);
                        Assert.AreEqual(S.EmberPressureCount, after.Args.GetInt("count", 0, 0, 99));
                        Assert.AreEqual(S.EmberCount, before.Args.GetInt("count", 0, 0, 99));
                        Assert.IsTrue(S.EmberPressureEverySeconds < S.EmberEverySeconds && S.EmberPressureSpeed > S.EmberSpeed && S.EmberPressureCount > S.EmberCount, "denser and faster");
                        Assert.AreEqual(before.Options, after.Options);
                        break;
                    }
                }

                // a second fire of the timer (it is never re-armed) changes nothing
                p.ClearRecordings();
                Fire(s, p, HarbingerScript.TimerPressure);
                Assert.AreEqual(0, p.Said.Count, def.Tag);
                Assert.AreEqual(0, p.Hazards.Count, def.Tag);
            }
        }

        [TestMethod]
        public void LinesAreTheOrchestratorText_Ascii()
        {
            Assert.AreEqual("Rise, warden. The door is not yet yours to take.", HarbingerLines.LineTwinRevive);
            Assert.AreEqual("You linger at my door. Then let the alcove remember whose hall this is.", HarbingerLines.LinePressure);

            foreach (var line in new[] { HarbingerLines.LineTwinRevive, HarbingerLines.LinePressure })
                Assert.IsTrue(line.All(ch => ch >= 0x20 && ch < 0x7F), line);
        }

        // ================================================================ Whiteout (frost)

        [TestMethod]
        public void Whiteout_TheRuntimeReceives_RainOnTheFrostAlcove_WithTheFloorHailsSlow()
        {
            var (_, p) = Arrived(HarbingerElement.Frost);
            var run = SigRuns(p).Single();
            var a = run.Args;

            Assert.AreEqual("sig-frost", run.Tag);
            Assert.AreEqual(HazardMechanicRegistry.Rain, run.Kind);
            Assert.AreEqual(HazardRegionPresets.HarbingerAlcoveSouthName, run.Options.Region, "frost is the south alcove");
            Assert.IsNull(run.Options.Area);
            Assert.AreEqual(S.HailSlow, run.Options.HitSlow, "the floor hail's slow");
            Assert.AreEqual("cold", a.GetString("damage_type"));
            Assert.AreEqual(HazardLevelScale.Scaled(p.Level, S.WhiteoutPct, S.HazardPctMin, S.HazardPctMax), a.GetDouble("pct", 0, 0, 9), 1e-12, "pct through the level scale");
            Assert.AreEqual(0.4, a.GetDouble("ball_radius", 0, 0, 9), 1e-12, "the small hailstone");
            Assert.AreEqual((int)HarbingerData.HailstoneWcid, a.GetInt("ball_wcid", 0, 0, int.MaxValue));

            var rs = RainSettings.From(a);
            Assert.IsTrue(rs.DropZ.Value + rs.BallRadius <= HazardRegionPresets.HarbingerAlcoveCeilingZ - 0.5, $"drop top {rs.DropZ + rs.BallRadius} under the 9.0 ceiling");
            Assert.IsNull(RainRules.Validate(Resolve(run), a, null), "the rain validates on its region");
        }

        // ================================================================ Ember roll (fire)

        [TestMethod]
        public void EmberRoll_TheRuntimeReceives_SmallSlowBalls_OnTheFireAlcove_InEveryDirection()
        {
            var (_, p) = Arrived(HarbingerElement.Fire);
            var run = SigRuns(p).Single();
            var a = run.Args;
            var floorRun = p.Hazards.First(h => h.Tag == Fire_.Tag);

            Assert.AreEqual("sig-fire", run.Tag);
            Assert.AreEqual(HazardMechanicRegistry.Balls, run.Kind);
            Assert.AreEqual(BallPatterns.Random, run.Pattern);
            Assert.AreEqual(HazardRegionPresets.HarbingerAlcoveNorthName, run.Options.Region, "fire is the north alcove");
            Assert.IsNull(run.Options.Area);
            Assert.IsNull(run.Options.HitSlow);
            Assert.AreEqual("fire", a.GetString("damage_type"));
            Assert.AreEqual(BallPatterns.AxisAny, a.GetString("axis"), "round 4: balls roll in every direction");
            Assert.AreEqual(S.EmberCount, a.GetInt("count", 0, 0, 99), "round 3: two balls a wave");
            Assert.AreEqual(S.EmberPctAt(p.Level), a.GetDouble("pct", 0, 0, 9), 1e-12, "round 3: the linear 3-10% clamp");

            // smaller than the floor run's: the floor run gives no ball_radius, so its balls are the default 1.8 m
            var ember = BallRules.Read(a, BallPatterns.Random);
            var floorBalls = BallRules.Read(floorRun.Args, floorRun.Pattern);
            Assert.IsFalse(floorBalls.BallRadiusGiven);
            Assert.AreEqual(HazardDefaults.RollingDeathRadius, floorBalls.BallRadius);
            Assert.IsTrue(ember.BallRadiusGiven && ember.BallRadius <= floorBalls.BallRadius / 3f, $"ember ball {ember.BallRadius} m vs the floor's {floorBalls.BallRadius} m");

            Assert.IsNull(BallRules.Validate(Resolve(run), a, BallPatterns.Random), "the balls validate on the fire alcove");
        }

        // ================================================================ Corrosion pools (acid)

        [TestMethod]
        public void Pool_TheRuntimeReceives_AQuietStillFill_OnItsSlotsArea()
        {
            var (s, p) = Arrived(HarbingerElement.Acid);
            Drip(s, p);

            var run = SigRuns(p).Single();
            var a = run.Args;
            var slot = int.Parse(run.Tag.Substring(HarbingerScript.TagPoolPrefix.Length));
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Acid);

            Assert.AreEqual(HazardMechanicRegistry.Fill, run.Kind);
            Assert.IsNull(run.Options.Region);
            Assert.AreEqual(HarbingerLayout.PoolArea(alcove, slot, S.PoolSize), run.Options.Area);
            Assert.AreEqual(S.PoolSize, run.Options.Area.MaxX - run.Options.Area.MinX, 1e-5);
            Assert.AreEqual(S.PoolSize, run.Options.Area.MaxY - run.Options.Area.MinY, 1e-5);

            Assert.IsTrue(a.GetBool("quiet", false), "no rising line per pool");
            Assert.AreEqual(0.0, a.GetDouble("rate", 1, -9, 9));
            Assert.AreEqual(0, a.GetInt("wisps", 1, 0, 99));
            Assert.IsFalse(a.GetBool("doubled", true));
            Assert.AreEqual("acid", a.GetString("damage_type"));

            var region = Resolve(run);
            var fs = FillSettings.From(a, region);
            Assert.IsTrue(fs.Quiet);
            Assert.AreEqual(HazardRegionPresets.HarbingerAlcoveGroundZ, fs.GroundZ, 1e-6);
            Assert.IsTrue(fs.StartZ > fs.GroundZ && fs.StartZ - fs.GroundZ <= 0.25, $"a low pool surface: {fs.StartZ}");
            Assert.IsTrue(fs.MaxZ >= fs.FloorZ + 0.5, "FillRules: max_z at least 0.5 over floor_z");
            Assert.AreEqual((S.PoolDamage + S.PoolDamagePerM * HarbingerSettings.PoolSurfaceZ) * HazardLevelScale.BenchmarkPct(p.Level, S.HazardPctMin, S.HazardPctMax) / HazardLevelScale.AnchorPct,
                FillRules.FlatDamage(fs.StartZ - fs.GroundZ, fs, fs.DamageScale), 1e-9, "the depth curve at a fresh pool's depth, through the acid's level scale");
            Assert.IsNull(FillRules.Validate(region, a), "the pool validates on its area");

            // one panel, the area itself (sheet_margin 0): the surface IS the damage area
            var (edge, tiles) = FillRules.Sheet(region, fs.PanelSize, fs.SheetMargin);
            Assert.AreEqual(1, tiles.Count);
            Assert.AreEqual(S.PoolSize, edge, 1e-5);
        }

        // ---- the alcove arrival grace (round 4) ----

        private static bool AnyHazardOtherThanFilm(FakePinnacleFightPort p) => p.Hazards.Any(h => (IsSig(h.Tag) || h.Tag.StartsWith(HarbingerScript.TagDripPrefix)) && h.Tag != HarbingerScript.TagFilm);

        [TestMethod]
        public void Grace_EveryAlcove_StartsNoHazardUntilTheGraceTimerFires_ThenStartsItsSignature()
        {
            foreach (var def in HarbingerData.Elements)
            {
                var (s, p) = Started();
                ToAlcove(s, p, def.Element);
                ArriveRaw(s, p, def.Element);

                Assert.AreEqual(10.0, LastTimer(p, HarbingerScript.TimerGrace), def.Tag);
                Assert.AreEqual(S.PressureSeconds, LastTimer(p, HarbingerScript.TimerPressure), def.Tag + ": the pressure still counts from the arrival");
                Assert.IsFalse(AnyHazardOtherThanFilm(p), $"{def.Tag}: no hazard during the grace");
                Assert.IsFalse(p.Timers.ContainsKey(HarbingerScript.TimerDrip), $"{def.Tag}: no drip clock during the grace");

                // later polls with the owner inside start nothing and arm no second grace
                Fire(s, p, HarbingerScript.TimerPoll);
                Assert.IsFalse(AnyHazardOtherThanFilm(p), def.Tag);
                Assert.AreEqual(1, p.TimerSets.Count(t => t.Tag == HarbingerScript.TimerGrace), def.Tag);

                Fire(s, p, HarbingerScript.TimerGrace);

                if (def.Element == HarbingerElement.Acid)
                    Assert.IsTrue(p.Timers.ContainsKey(HarbingerScript.TimerDrip), "the drips start with the grace's end");
                else
                    Assert.AreEqual(HarbingerScript.SignatureTag(def), SigRuns(p).Single().Tag, def.Tag);

                // fired twice (a stale event): nothing doubles
                Fire(s, p, HarbingerScript.TimerGrace);
                Assert.AreEqual(def.Element == HarbingerElement.Acid ? 0 : 1, SigRuns(p).Count, def.Tag);
            }
        }

        [TestMethod]
        public void Grace_TheHarmlessFilm_ShowsAtOnce_ADamagingFilmWaitsWithTheRest()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Acid);
            ArriveRaw(s, p, HarbingerElement.Acid);
            Assert.AreEqual(1, FilmRuns(p).Count, "film_damage 0: a picture, so it starts at the arrival");

            var (s2, p2) = Started(S with { FilmDamage = 5 });
            ToAlcove(s2, p2, HarbingerElement.Acid);
            ArriveRaw(s2, p2, HarbingerElement.Acid);
            Assert.AreEqual(0, FilmRuns(p2).Count, "a damaging film waits for the grace");
            Fire(s2, p2, HarbingerScript.TimerGrace);
            Assert.AreEqual(1, FilmRuns(p2).Count);
        }

        [TestMethod]
        public void Grace_ARearrivalAfterADeathOrAResume_GetsItsOwnGrace()
        {
            foreach (var e in new[] { HarbingerElement.Acid, HarbingerElement.Frost })
            {
                var (s, p) = Arrived(e);   // the grace ran out
                p.ClearRecordings();

                // the resume (RestartPhase after a death): the signature and its grace stop
                s.RestartPhase(p);
                CollectionAssert.Contains(p.CancelledTimers, HarbingerScript.TimerGrace, e.ToString());
                Assert.IsFalse(AnyHazardOtherThanFilm(p), e.ToString());

                // the next arrival waits another full grace
                p.ClearRecordings();
                ArriveRaw(s, p, e);
                Assert.AreEqual(10.0, LastTimer(p, HarbingerScript.TimerGrace), e.ToString());
                Assert.IsFalse(AnyHazardOtherThanFilm(p), e + ": nothing live during the second grace");
                Fire(s, p, HarbingerScript.TimerGrace);
                Assert.IsTrue(e == HarbingerElement.Acid ? p.Timers.ContainsKey(HarbingerScript.TimerDrip) : SigRuns(p).Count == 1, e.ToString());
            }
        }

        [TestMethod]
        public void Grace_AGraceTimerFiringAfterTheSignatureStopped_StartsNothing()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Frost);
            ArriveRaw(s, p, HarbingerElement.Frost);
            p.ClearRecordings();
            s.RestartPhase(p);
            p.ClearRecordings();

            Fire(s, p, HarbingerScript.TimerGrace);
            Assert.AreEqual(0, p.Hazards.Count);
        }

        [TestMethod]
        public void Grace_AGuardianDyingDuringIt_LeavesItsPoolWhenTheGraceEnds_NotBefore()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Acid);
            ArriveRaw(s, p, HarbingerElement.Acid);
            var guards = Guards(p, Acid);
            var at = HarbingerLayout.PoolSlot(HarbingerData.AlcoveOf(HarbingerElement.Acid), 5);
            p.PositionOf[guards[0]] = Pt(at.X, at.Y, 0f);
            Fire(s, p, HarbingerScript.TimerPoll);
            Died(s, p, Acid.GuardTag, guards[0]);
            Assert.AreEqual(0, PoolSlots(p).Count, "no pool during the grace");
            Assert.AreEqual(0, p.Hazards.Count(h => h.Tag.StartsWith(HarbingerScript.TagPoolPrefix)));

            // the grace ends: the pool the guardian's death owes is placed now, at the slot nearest where it fell
            Fire(s, p, HarbingerScript.TimerGrace);
            Assert.AreEqual(1, PoolSlots(p).Count, "the deferred pool appears at the grace's end");
            Assert.AreEqual(5, PoolSlots(p).Single(), "at the slot where it fell");

            // a second guardian dying after the grace leaves its pool at once, as before
            p.PositionOf[guards[1]] = Pt(HarbingerLayout.PoolSlot(HarbingerData.AlcoveOf(HarbingerElement.Acid), 10).X, HarbingerLayout.PoolSlot(HarbingerData.AlcoveOf(HarbingerElement.Acid), 10).Y, 0f);
            Fire(s, p, HarbingerScript.TimerPoll);
            Died(s, p, Acid.GuardTag, guards[1]);
            Assert.AreEqual(2, PoolSlots(p).Count, "a pool at once once the grace is over");
        }

        [TestMethod]
        public void Grace_DeferredPools_AreForgottenByAResume()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Acid);
            ArriveRaw(s, p, HarbingerElement.Acid);
            var guards = Guards(p, Acid);
            var at = HarbingerLayout.PoolSlot(HarbingerData.AlcoveOf(HarbingerElement.Acid), 5);
            p.PositionOf[guards[0]] = Pt(at.X, at.Y, 0f);
            Fire(s, p, HarbingerScript.TimerPoll);
            Died(s, p, Acid.GuardTag, guards[0]);

            s.RestartPhase(p);
            ArriveRaw(s, p, HarbingerElement.Acid);
            p.ClearRecordings();
            Fire(s, p, HarbingerScript.TimerGrace);
            Assert.AreEqual(0, PoolSlots(p).Count, "the old death does not leave a pool in the next grace");
        }

        [TestMethod]
        public void Grace_PressureBeforeTheGraceEnds_DoesNotStartTheDrips_AndTheDripsStartPressed()
        {
            var (s, p) = Started(S with { PressureSeconds = 5, AlcoveGraceSeconds = 20 });
            ToAlcove(s, p, HarbingerElement.Acid);
            ArriveRaw(s, p, HarbingerElement.Acid);

            Fire(s, p, HarbingerScript.TimerPressure);
            Assert.IsFalse(p.Timers.ContainsKey(HarbingerScript.TimerDrip), "pressure during the grace starts no drip clock");

            Fire(s, p, HarbingerScript.TimerGrace);
            Assert.AreEqual(S.DripPressureSeconds, LastTimer(p, HarbingerScript.TimerDrip), "the drips start at the pressed interval");
        }

        [TestMethod]
        public void Grace_TheCrystalsFirstCast_LandsNoEarlierThanTheGraceEnds_AndTheLaterCadenceIsUnchanged()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Frost);
            ArriveRaw(s, p, HarbingerElement.Frost);

            Assert.AreEqual(10.0, LastTimer(p, HarbingerScript.TimerCrystalCast), "max(8 s, the 10 s grace)");

            // the cast fires, and the next one is the normal 8 s
            Fire(s, p, HarbingerScript.TimerCrystalCast);
            Assert.AreEqual(S.CrystalCastSeconds, LastTimer(p, HarbingerScript.TimerCrystalCast));

            // a guardian dying DURING a grace halves the cadence but still never casts inside it
            var (s2, p2) = Started();
            ToAlcove(s2, p2, HarbingerElement.Frost);
            ArriveRaw(s2, p2, HarbingerElement.Frost);
            p2.Now += 4.0;
            Died(s2, p2, Frost.GuardTag, Guards(p2, Frost)[0]);
            Assert.AreEqual(6.0, LastTimer(p2, HarbingerScript.TimerCrystalCast), 1e-9, "4 s (halved) but the grace has 6 s left");

            // after the grace the ward link halves it to 4 s as before
            var (s3, p3) = Arrived(HarbingerElement.Frost);
            Died(s3, p3, Frost.GuardTag, Guards(p3, Frost)[0]);
            Assert.AreEqual(S.CrystalCastSeconds / 2, LastTimer(p3, HarbingerScript.TimerCrystalCast), 1e-9);
        }

        [TestMethod]
        public void Grace_Zero_TheCrystalsFirstCast_IsTheNormalDelay()
        {
            var (s, p) = Started(S with { AlcoveGraceSeconds = 0 });
            ToAlcove(s, p, HarbingerElement.Frost);
            ArriveRaw(s, p, HarbingerElement.Frost);
            Assert.AreEqual(S.CrystalCastSeconds, LastTimer(p, HarbingerScript.TimerCrystalCast));

            var (s2, p2) = Started(S with { AlcoveGraceSeconds = 3 });
            ToAlcove(s2, p2, HarbingerElement.Frost);
            ArriveRaw(s2, p2, HarbingerElement.Frost);
            Assert.AreEqual(S.CrystalCastSeconds, LastTimer(p2, HarbingerScript.TimerCrystalCast), "a grace shorter than the cast delay changes nothing");
        }

        [TestMethod]
        public void Grace_Zero_StartsEverythingAtTheArrival_AsBeforeRound4()
        {
            var (s, p) = Started(S with { AlcoveGraceSeconds = 0 });
            ToAlcove(s, p, HarbingerElement.Fire);
            ArriveRaw(s, p, HarbingerElement.Fire);

            Assert.IsFalse(p.Timers.ContainsKey(HarbingerScript.TimerGrace));
            Assert.AreEqual(1, SigRuns(p).Count);
        }

        [TestMethod]
        public void Grace_IsAnOverride_ZeroToSixty()
        {
            Assert.IsTrue(HarbingerOverrides.TryApply(S, HazardArgs.Parse("alcove_grace_seconds=25"), out var applied, out var e1), e1);
            Assert.AreEqual(25.0, applied.AlcoveGraceSeconds);

            var (s, p) = Started(applied);
            ToAlcove(s, p, HarbingerElement.Frost);
            ArriveRaw(s, p, HarbingerElement.Frost);
            Assert.AreEqual(25.0, LastTimer(p, HarbingerScript.TimerGrace), "the override reaches the timer the runtime is given");

            Assert.IsFalse(HarbingerOverrides.TryApply(S, HazardArgs.Parse("alcove_grace_seconds=61"), out _, out _));
            Assert.IsFalse(HarbingerOverrides.TryApply(S, HazardArgs.Parse("alcove_grace_seconds=-1"), out _, out _));
        }

        // ---- the drip clock catches up (round 4) ----

        /// <summary>Simulates <paramref name="seconds"/> of 1 s ticks: the fake clock advances a tick and a due drip timer fires. Returns the drips per tick.</summary>
        private static List<int> SimulateDripTicks(HarbingerScript s, FakePinnacleFightPort p, int seconds)
        {
            var perTick = new List<int>();

            for (var t = 0; t < seconds; t++)
            {
                p.Now += 1.0;
                var before = p.Hazards.Count(h => h.Tag.StartsWith(HarbingerScript.TagDripPrefix));

                if (p.Timers.TryGetValue(HarbingerScript.TimerDrip, out var due) && due <= p.Now + 1e-9)
                {
                    p.Timers.Remove(HarbingerScript.TimerDrip);
                    Fire(s, p, HarbingerScript.TimerDrip);
                }

                var after = p.Hazards.Where(h => h.Tag.StartsWith(HarbingerScript.TagDripPrefix)).ToList();
                perTick.Add(after.Count - before);

                // each drip lands at once, so none stays in the air
                foreach (var d in after.Skip(before))
                    Ended(s, p, d.Tag, HazardOutcome.Completed);
            }

            return perTick;
        }

        [TestMethod]
        public void DripClock_Unpressed_AveragesTheDripInterval_OverASimulatedRun()
        {
            var (s, p) = Arrived(HarbingerElement.Acid, S with { PoolCap = 18 });
            var ticks = SimulateDripTicks(s, p, 600);

            Assert.AreEqual(300, ticks.Sum(), 1, "2 s: 300 drips in 600 s");
            Assert.IsTrue(ticks.All(n => n <= HarbingerScript.MaxDripsPerTick));
        }

        [TestMethod]
        public void DripClock_Pressed_AveragesTheDripPressureInterval_NotTheTickRoundedUpOne()
        {
            var (s, p) = Arrived(HarbingerElement.Acid, S with { PoolCap = 18 });
            Fire(s, p, HarbingerScript.TimerPressure);
            Assert.AreEqual(S.DripPressureSeconds, LastTimer(p, HarbingerScript.TimerDrip));

            var ticks = SimulateDripTicks(s, p, 600);
            var expected = 600.0 / S.DripPressureSeconds;

            Assert.AreEqual(expected, ticks.Sum(), 2.0, $"1.2 s: about {expected:0} drips in 600 s, not the 300 of a 2 s cadence");
            Assert.IsTrue(ticks.All(n => n <= HarbingerScript.MaxDripsPerTick), "at most two a tick");
            Assert.IsTrue(ticks.Any(n => n == 0), "the remainder is carried: some ticks owe nothing");
        }

        [TestMethod]
        public void DripClock_ABacklogPastTheCap_IsDroppedNotRepaid()
        {
            var (s, p) = Arrived(HarbingerElement.Acid, S with { PoolCap = 18 });
            Fire(s, p, HarbingerScript.TimerPressure);

            // the clock jumps 30 s in one tick: two drips fire, the rest of the backlog is dropped
            p.Now += 30.0;
            p.Timers.Remove(HarbingerScript.TimerDrip);
            var before = p.Hazards.Count(h => h.Tag.StartsWith(HarbingerScript.TagDripPrefix));
            Fire(s, p, HarbingerScript.TimerDrip);
            Assert.AreEqual(HarbingerScript.MaxDripsPerTick, p.Hazards.Count(h => h.Tag.StartsWith(HarbingerScript.TagDripPrefix)) - before);

            // the backlog is gone, not repaid: the interval starts afresh, so the re-armed timer is one pressed interval out (not 0)
            Assert.AreEqual(S.DripPressureSeconds, p.Timers[HarbingerScript.TimerDrip] - p.Now, 1e-9, "re-armed one pressed interval from now");

            // and the next tick, firing the timer again, owes at most one drip
            p.Now += 1.0;
            before = p.Hazards.Count(h => h.Tag.StartsWith(HarbingerScript.TagDripPrefix));
            Fire(s, p, HarbingerScript.TimerDrip);
            Assert.IsTrue(p.Hazards.Count(h => h.Tag.StartsWith(HarbingerScript.TagDripPrefix)) - before <= 1, "the backlog is not repaid");
        }

        [TestMethod]
        public void DripClock_FiredInsideTheGrace_DripsNothing()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Acid);
            ArriveRaw(s, p, HarbingerElement.Acid);
            p.Now += 30.0;

            Fire(s, p, HarbingerScript.TimerDrip);
            Assert.AreEqual(0, p.Hazards.Count(h => h.Tag.StartsWith(HarbingerScript.TagDripPrefix)), "a stray drip timer during the grace drips nothing");
            Assert.IsFalse(p.Timers.ContainsKey(HarbingerScript.TimerDrip), "and is not re-armed");
        }

        [TestMethod]
        public void Film_ValidatesAtBothFilmDamageBounds_SoAConstantEditFailsATest()
        {
            // no override can make the film's fill refuse (film_damage is only a flat damage), so SignatureProblem has no film block;
            // this pins the call it would have made, on the real area, at the bounds
            var acid = HarbingerData.AlcoveOf(HarbingerElement.Acid);
            var area = PinnacleHazardRegions.Resolve(new PinnacleHazardOptions(Area: HarbingerLayout.FilmArea(acid)), HazardRegionPresets.HarbingerName, 0, out var error);
            Assert.IsNotNull(area, error);

            foreach (var damage in new[] { 0.0, 1000.0 })
                Assert.IsNull(FillRules.Validate(area, (S with { FilmDamage = damage }).FilmArgs(5.0)), $"film_damage {damage}");

            Assert.IsNull(HarbingerOverrides.SignatureProblem(S with { FilmDamage = 1000 }));
        }

        // ---- the acid film (round 4) ----

        [TestMethod]
        public void Film_StartsWithTheAcidSignature_OnTheWholeFloor_AsAQuietStillFill_ThatDealsNothingByDefault()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Acid);
            Assert.AreEqual(0, FilmRuns(p).Count, "no film at the threshold");

            Arrive(s, p, HarbingerElement.Acid);

            var run = FilmRuns(p).Single();
            var a = run.Args;
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Acid);

            Assert.AreEqual("sig-film", run.Tag);
            Assert.AreEqual(HazardMechanicRegistry.Fill, run.Kind);
            Assert.IsNull(run.Options.Region);
            Assert.AreEqual(HarbingerLayout.FilmArea(alcove), run.Options.Area);

            // the 9 m square: twice the alcove wall distance, centred on the alcove
            Assert.AreEqual(2f * HazardRegionPresets.HarbingerAlcoveWallDistance, run.Options.Area.MaxX - run.Options.Area.MinX, 1e-5);
            Assert.AreEqual(9f, run.Options.Area.MaxY - run.Options.Area.MinY, 1e-5);
            Assert.AreEqual(alcove.Centre.X, (run.Options.Area.MinX + run.Options.Area.MaxX) / 2f, 1e-4);
            Assert.AreEqual(alcove.Centre.Y, (run.Options.Area.MinY + run.Options.Area.MaxY) / 2f, 1e-4);

            Assert.IsTrue(a.GetBool("quiet", false));
            Assert.AreEqual(0.0, a.GetDouble("rate", 1, -9, 9), "still");
            Assert.AreEqual(0, a.GetInt("wisps", 1, 0, 99));
            Assert.IsFalse(a.GetBool("doubled", true));
            Assert.AreEqual("acid", a.GetString("damage_type"));

            var region = Resolve(run);
            var fs = FillSettings.From(a, region);

            Assert.IsNull(FillRules.Validate(region, a), "the film validates on its area");
            Assert.AreEqual(HarbingerSettings.FilmSurfaceZ, fs.StartZ, 1e-9);
            Assert.IsTrue(fs.StartZ > fs.FloorZ && fs.StartZ < HarbingerSettings.PoolSurfaceZ, $"above its floor, below a fresh pool's surface: {fs.StartZ}");

            // the runtime receives no damage at any depth, at any level scale
            Assert.AreEqual(0.0, S.FilmDamage);
            Assert.AreEqual(0.0, FillRules.FlatDamage(fs.StartZ - fs.GroundZ, fs, fs.DamageScale), 1e-12);
            Assert.AreEqual(0.0, FillRules.FlatDamage(1.0, fs, 5.0), 1e-12);

            // one panel, the area itself
            var (edge, tiles) = FillRules.Sheet(region, fs.PanelSize, fs.SheetMargin);
            Assert.AreEqual(1, tiles.Count);
            Assert.AreEqual(9f, edge, 1e-5);

            // later polls never start a second one
            Fire(s, p, HarbingerScript.TimerPoll);
            Assert.AreEqual(1, FilmRuns(p).Count);
        }

        [TestMethod]
        public void Film_OnlyTheAcidAlcove_HoldsNoPoolSlot_AndIsNotCountedByThePoolCap()
        {
            foreach (var e in new[] { HarbingerElement.Lightning, HarbingerElement.Frost, HarbingerElement.Fire })
            {
                var (_, p) = Arrived(e);
                Assert.AreEqual(0, FilmRuns(p).Count, e.ToString());
            }

            var (s, a) = Arrived(HarbingerElement.Acid, S with { PoolCap = 1 });
            StringAssert.Contains(s.Describe(), "pools=0");
            Assert.AreEqual(1, FilmRuns(a).Count);

            Drip(s, a);
            Drip(s, a);
            Drip(s, a);
            Assert.AreEqual(1, PoolSlots(a).Count, "the cap of 1 still admits its one pool beside the film");
            StringAssert.Contains(s.Describe(), "pools=1");
        }

        [TestMethod]
        public void Film_RestartsLikeAPool_WhenItsRunEnds_StopsWithTheSignature_AndStartsAgainOnTheNextArrival()
        {
            var (s, p) = Arrived(HarbingerElement.Acid);

            // a run that ended (the host's one-retry policy): restarted through the one builder, same args and area
            var first = FilmRuns(p).Single();
            Ended(s, p, "sig-film", HazardOutcome.Aborted);
            Assert.AreEqual(2, FilmRuns(p).Count);
            Assert.AreEqual(first.Args.ToString(), FilmRuns(p).Last().Args.ToString());
            Assert.AreEqual(first.Options.Area, FilmRuns(p).Last().Options.Area);

            // past the restart cap it is given up, like a pool
            for (var i = 0; i < HarbingerScript.MaxRunRestarts; i++)
                Ended(s, p, "sig-film", HazardOutcome.Aborted);

            var given = FilmRuns(p).Count;
            Ended(s, p, "sig-film", HazardOutcome.Aborted);
            Assert.AreEqual(given, FilmRuns(p).Count, "no restart past the cap");

            // a hold and the resume: stopped with the signature, and only the next arrival starts it again
            var (s2, p2) = Arrived(HarbingerElement.Acid);
            p2.ClearRecordings();
            s2.RestartPhase(p2);
            CollectionAssert.Contains(p2.StoppedHazardTags, "sig-film");
            Assert.AreEqual(0, FilmRuns(p2).Count, "the resume starts no film");

            Ended(s2, p2, "sig-film", HazardOutcome.Aborted);
            Assert.AreEqual(0, FilmRuns(p2).Count, "a queued end of the old film is not wanted");

            Arrive(s2, p2, HarbingerElement.Acid);
            Assert.AreEqual(1, FilmRuns(p2).Count, "the next arrival starts it again");
        }

        [TestMethod]
        public void Film_Damage_IsAnOverride_AndReachesTheRuntimeAtTheLevelScale()
        {
            Assert.IsTrue(HarbingerOverrides.TryApply(S, HazardArgs.Parse("film_damage=12"), out var applied, out var error), error);
            Assert.AreEqual(12.0, applied.FilmDamage);

            var (_, p) = Arrived(HarbingerElement.Acid, applied);
            var run = FilmRuns(p).Single();
            var region = Resolve(run);
            var fs = FillSettings.From(run.Args, region);

            Assert.IsNull(FillRules.Validate(region, run.Args));
            var scale = HazardLevelScale.BenchmarkPct(p.Level, S.HazardPctMin, S.HazardPctMax) / HazardLevelScale.AnchorPct;
            Assert.AreEqual(12.0 * scale, FillRules.FlatDamage(0.5, fs, fs.DamageScale), 1e-9, "flat, whatever the depth");

            Assert.IsFalse(HarbingerOverrides.TryApply(S, HazardArgs.Parse("film_damage=1001"), out _, out _));
        }

        [TestMethod]
        public void Drips_AtTheRound4Cadence_OverlappingInTheAir_KeepUniqueTags_AndEachLandsOnItsOwnSlot()
        {
            Assert.AreEqual(2.0, S.DripSeconds);
            Assert.AreEqual(1.2, S.DripPressureSeconds);

            var (s, p) = Arrived(HarbingerElement.Acid);
            var tags = new List<string> { Drip(s, p, land: false), Drip(s, p, land: false), Drip(s, p, land: false) };

            Assert.AreEqual(3, tags.Distinct().Count(), "one tag per drip, so runs that overlap never collide");
            Assert.IsTrue(tags.All(t => t.StartsWith(HarbingerScript.TagDripPrefix)));

            foreach (var t in tags)
                Ended(s, p, t, HazardOutcome.Completed);

            Assert.IsTrue(PoolSlots(p).Count >= 1, "the landed drips raised pools");
        }

        [TestMethod]
        public void Drips_StartPoolsOnFreeSlotsDrawnWithTheFightsRng_NeverStack_AndStopAtTheCap()
        {
            var (s, p) = Arrived(HarbingerElement.Acid);
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Acid);

            for (var i = 0; i < 60; i++)
                Drip(s, p);

            var slots = PoolSlots(p);
            Assert.AreEqual(S.PoolCap, slots.Count, "capped");
            Assert.AreEqual(S.PoolCap, SigRuns(p).Count, "one run per pool: a deepening drip starts no run");
            Assert.AreEqual(slots.Count, slots.Distinct().Count());

            for (var i = 0; i < slots.Count; i++)
                for (var j = i + 1; j < slots.Count; j++)
                {
                    var a = HarbingerLayout.PoolSlot(alcove, slots[i]);
                    var b = HarbingerLayout.PoolSlot(alcove, slots[j]);
                    Assert.IsFalse(Math.Abs(a.X - b.X) < S.PoolSize - 1e-3 && Math.Abs(a.Y - b.Y) < S.PoolSize - 1e-3, $"slots {slots[i]} and {slots[j]} overlap");
                }

            // the draw is the fight's Rng: the same seed drips the same slots, another seed (here) others
            var (s2, p2) = Arrived(HarbingerElement.Acid);
            for (var i = 0; i < 60; i++)
                Drip(s2, p2);

            CollectionAssert.AreEqual(slots, PoolSlots(p2));

            var (s3, p3) = Arrived(HarbingerElement.Acid, seed: 99);
            for (var i = 0; i < 60; i++)
                Drip(s3, p3);

            CollectionAssert.AreNotEqual(slots, PoolSlots(p3));
            StringAssert.Contains(s.Describe(), $"pools={S.PoolCap}");
        }

        /// <summary>True when two live pool runs' areas overlap with positive area (touching edges do not).</summary>
        private static List<string> Overlaps(FakePinnacleFightPort p)
        {
            var live = p.Hazards.Where(h => h.Tag.StartsWith(HarbingerScript.TagPoolPrefix) && !p.StoppedHazardTags.Contains(h.Tag))
                .GroupBy(h => h.Tag).Select(g => g.Last()).ToList();
            var bad = new List<string>();

            for (var i = 0; i < live.Count; i++)
                for (var j = i + 1; j < live.Count; j++)
                {
                    var a = live[i].Options.Area;
                    var b = live[j].Options.Area;
                    var ox = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
                    var oy = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);

                    if (ox > 1e-3f && oy > 1e-3f)
                        bad.Add($"{live[i].Tag}/{live[j].Tag} overlap {ox:0.###} x {oy:0.###}");
                }

            return bad;
        }

        [TestMethod]
        public void Pools_NeverOverlap_AtTheSillPair_AndAtPoolSize3()
        {
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Acid);
            const int sillA = 16, sillB = 17;
            Assert.AreEqual(-4.0f, HarbingerLayout.PoolSlotsUV[sillA].X, "slot 16 is a sill slot");
            Assert.AreEqual(-4.0f, HarbingerLayout.PoolSlotsUV[sillB].X, "slot 17 is a sill slot");

            // the sill pair: a guardian falls on each sill slot in turn; the second lands at the nearest slot that does not overlap
            var (s, p) = Arrived(HarbingerElement.Acid);
            var guards = Guards(p, Acid);
            var a = HarbingerLayout.PoolSlot(alcove, sillA);
            var b = HarbingerLayout.PoolSlot(alcove, sillB);
            p.PositionOf[guards[0]] = Pt(a.X, a.Y, 0f);
            p.PositionOf[guards[1]] = Pt(b.X, b.Y, 0f);
            Fire(s, p, HarbingerScript.TimerPoll);

            Died(s, p, Acid.GuardTag, guards[0]);
            Assert.AreEqual(HarbingerScript.PoolTag(sillA), SigRuns(p).Single().Tag);

            // guard 1 stands just outside pool 16's square but on slot 17: slot 17 overlaps 16, so it takes the nearest free one
            Died(s, p, Acid.GuardTag, guards[1]);
            var runs = SigRuns(p);
            Assert.AreEqual(2, runs.Count, "a pool still falls (the nearest-free fallback)");
            Assert.AreNotEqual(HarbingerScript.PoolTag(sillB), runs[1].Tag, "never the overlapping sill twin");
            CollectionAssert.AreEqual(new List<string>(), Overlaps(p));

            // dripping to the cap never overlaps either, at the default and at the largest pool
            foreach (var size in new[] { S.PoolSize, 3.0 })
            {
                foreach (var seed in Enumerable.Range(1, 20))
                {
                    var (s2, p2) = Arrived(HarbingerElement.Acid, S with { PoolSize = size, PoolCap = HarbingerOverrides.PoolSlotCount }, seed);

                    for (var i = 0; i < 3 * HarbingerOverrides.PoolSlotCount; i++)
                        Drip(s2, p2);

                    Assert.IsTrue(SigRuns(p2).Count >= 2, $"size {size} seed {seed}: {SigRuns(p2).Count} pools");
                    CollectionAssert.AreEqual(new List<string>(), Overlaps(p2), $"size {size} seed {seed}");
                }
            }
        }

        [TestMethod]
        public void GuardianDeath_LeavesAPool_AtTheFreeSlotNearestWhereThePollLastSawIt()
        {
            var (s, p) = Arrived(HarbingerElement.Acid);
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Acid);
            var guards = Guards(p, Acid);

            // guard 0 walks to the far north-west of the alcove; the poll sees it there, then it dies
            var target = HarbingerLayout.PoolSlot(alcove, 3);
            p.PositionOf[guards[0]] = Pt(target.X + 0.2f, target.Y - 0.3f, 0f);
            Fire(s, p, HarbingerScript.TimerPoll);
            p.ClearRecordings();
            Died(s, p, Acid.GuardTag, guards[0]);

            var pool = SigRuns(p).Single();
            Assert.AreEqual(HarbingerScript.PoolTag(3), pool.Tag, "the slot nearest where it fell");
            Assert.AreEqual(HarbingerLayout.PoolArea(alcove, 3, S.PoolSize), pool.Options.Area);

            // guard 1 was never seen moving: its spawn point is where it fell
            p.ClearRecordings();
            Died(s, p, Acid.GuardTag, guards[1]);
            var second = SigRuns(p).Single();
            var spawn = alcove.Guards[1];
            var nearest = Enumerable.Range(0, HarbingerLayout.PoolSlotsUV.Count)
                .OrderBy(i => Vector2.Distance(HarbingerLayout.PoolSlot(alcove, i), new Vector2(spawn.X, spawn.Y))).ThenBy(i => i).First();
            Assert.AreEqual(HarbingerScript.PoolTag(nearest), second.Tag);
        }

        [TestMethod]
        public void GuardianDeath_OffTheAlcove_OrOnAPool_OrBeforeTheArrival_LeavesNoPool()
        {
            // fell to the chamber floor
            var (s, p) = Arrived(HarbingerElement.Acid);
            var guards = Guards(p, Acid);
            p.PositionOf[guards[0]] = Pt(10, -20, -36f);
            Fire(s, p, HarbingerScript.TimerPoll);
            p.ClearRecordings();
            Died(s, p, Acid.GuardTag, guards[0]);
            Assert.AreEqual(0, SigRuns(p).Count, "fell off the alcove");

            // onto a pool already there
            var (s2, p2) = Arrived(HarbingerElement.Acid);
            var g2 = Guards(p2, Acid);
            Drip(s2, p2);
            var weep = SigRuns(p2).Single();
            var area = weep.Options.Area;
            p2.PositionOf[g2[0]] = Pt((area.MinX + area.MaxX) / 2, (area.MinY + area.MaxY) / 2, 0f);
            Fire(s2, p2, HarbingerScript.TimerPoll);
            p2.ClearRecordings();
            Died(s2, p2, Acid.GuardTag, g2[0]);
            Assert.AreEqual(0, SigRuns(p2).Count, "acid is already there");

            // a guardian killed before the owner arrived (the signature is not on)
            var (s3, p3) = Started();
            ToAlcove(s3, p3, HarbingerElement.Acid);
            var g3 = Guards(p3, Acid);
            Died(s3, p3, Acid.GuardTag, g3[0]);
            Assert.AreEqual(0, SigRuns(p3).Count);
        }

        [TestMethod]
        public void Pool_AbandonedAfterTheRestartCap_FreesItsSlot()
        {
            var (s, p) = Arrived(HarbingerElement.Acid, S with { PoolCap = 1 });
            Drip(s, p);
            var tag = SigRuns(p).Single().Tag;

            for (var i = 0; i <= HarbingerScript.MaxRunRestarts; i++)
                Ended(s, p, tag, HazardOutcome.Aborted);

            StringAssert.Contains(s.Describe(), "pools=0");

            p.ClearRecordings();
            Drip(s, p);
            Assert.AreEqual(1, SigRuns(p).Count, "the cap counts live pools only");
        }

        // ================================================================ Twin conductors (lightning)

        [TestMethod]
        public void TwinConductors_AGuardKilledWhileItsTwinStands_RisesAgainAtItsPoint_AndTheWardHolds()
        {
            var (s, p) = Arrived(HarbingerElement.Lightning);
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Lightning);
            var crystal = Crystal(p, Lightning);
            var guards = Guards(p, Lightning);

            Died(s, p, Lightning.GuardTag, guards[0]);
            Assert.AreEqual(S.TwinReviveSeconds, LastTimer(p, HarbingerScript.TimerRevivePrefix + 0));
            Assert.IsFalse(Damageable(p, crystal), "one guard down: the ward holds");
            var linkedInterval = LastTimer(p, HarbingerScript.TimerCrystalCast);
            Assert.AreEqual(S.CrystalCastSeconds / 2, linkedInterval, 1e-9, "the ward link (U4) is set");

            p.ClearRecordings();
            Fire(s, p, HarbingerScript.TimerRevivePrefix + 0);

            var revived = p.Spawns.Single();
            Assert.AreEqual(Lightning.GuardTag, revived.Tag);
            Assert.AreEqual(Lightning.GuardianWcid, revived.Wcid);
            Assert.AreEqual(alcove.Guards[0], revived.At, "at its own spawn point");
            Assert.AreEqual(HarbingerData.AddTier, revived.Tier);
            CollectionAssert.Contains(p.Engaged, revived.Result);
            CollectionAssert.AreEqual(new[] { HarbingerLines.LineTwinRevive }, p.Said);
            Assert.IsFalse(Damageable(p, crystal), "the shield never dropped");
            Assert.AreEqual(0, p.CallsNamed(nameof(IPinnacleFightPort.SetImmune)).Count(), "nothing is re-shielded");
            StringAssert.Contains(s.Describe(), "guards=2 linked", "back to two guards, the ward link stays set until RespawnAlcove");

            // the revived guard holds the ward like any other: both down at once breaks it
            p.ClearRecordings();
            Died(s, p, Lightning.GuardTag, guards[1]);
            Assert.IsFalse(Damageable(p, crystal));
            Died(s, p, Lightning.GuardTag, revived.Result);
            Assert.IsTrue(Damageable(p, crystal), "both down at the same time");
            Assert.AreEqual(1, p.Said.Count(l => l == HarbingerLines.LineWardBroken));
        }

        [TestMethod]
        public void TwinConductors_BothDownBeforeTheRevive_BreaksTheWard_AndCancelsTheRevives()
        {
            var (s, p) = Arrived(HarbingerElement.Lightning);
            var crystal = Crystal(p, Lightning);
            var guards = Guards(p, Lightning);

            Died(s, p, Lightning.GuardTag, guards[0]);
            p.ClearRecordings();
            Died(s, p, Lightning.GuardTag, guards[1]);

            Assert.IsTrue(Damageable(p, crystal));
            Assert.AreEqual(HarbingerLines.LineWardBroken, p.Said.Single());
            CollectionAssert.Contains(p.CancelledTimers, HarbingerScript.TimerRevivePrefix + 0);
            CollectionAssert.Contains(p.CancelledTimers, HarbingerScript.TimerRevivePrefix + 1);
            Assert.IsFalse(p.TimerSets.Any(t => t.Tag == HarbingerScript.TimerRevivePrefix + 1), "the last death arms no revive");

            // a revive the core had already queued raises nothing: the ward is down, the twin is dead
            p.ClearRecordings();
            Fire(s, p, HarbingerScript.TimerRevivePrefix + 0);
            Assert.AreEqual(0, p.Spawns.Count);
            Assert.IsTrue(Damageable(p, crystal), "the crystal stays bare");
        }

        [TestMethod]
        public void TwinConductors_TheTwinDeadWhenTheReviveFires_RaisesNothing()
        {
            // guard 0 dies and arms its revive; the twin dies too but its AddDied has not been delivered yet when the revive
            // fires (it is still a ward entry): a revive needs a STANDING twin, not just a ward entry
            var (s, p) = Arrived(HarbingerElement.Lightning);
            var guards = Guards(p, Lightning);

            Died(s, p, Lightning.GuardTag, guards[0]);
            p.Kill(guards[1]);   // dead, its AddDied not delivered yet
            p.ClearRecordings();
            Fire(s, p, HarbingerScript.TimerRevivePrefix + 0);

            Assert.AreEqual(0, p.Spawns.Count, "the twin is not standing");
            CollectionAssert.DoesNotContain(p.Said, HarbingerLines.LineTwinRevive);
        }

        [TestMethod]
        public void TwinConductors_AFailedReviveLeavesTheWardAsItWas()
        {
            var (s, p) = Arrived(HarbingerElement.Lightning);
            var crystal = Crystal(p, Lightning);
            var guards = Guards(p, Lightning);

            Died(s, p, Lightning.GuardTag, guards[0]);
            p.FailNextSpawns = 1;
            p.ClearRecordings();
            Fire(s, p, HarbingerScript.TimerRevivePrefix + 0);
            CollectionAssert.DoesNotContain(p.Said, HarbingerLines.LineTwinRevive);

            // the core's SpawnFailed for the revive arrives: it arms no new revive and breaks nothing
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = Lightning.GuardTag, Handle = PinnacleHandle.None, SpawnFailed = true }, p);
            Assert.IsFalse(Damageable(p, crystal));
            Assert.AreEqual(0, p.TimerSets.Count(t => t.Tag.StartsWith(HarbingerScript.TimerRevivePrefix)));

            // the twin's death then breaks the ward
            Died(s, p, Lightning.GuardTag, guards[1]);
            Assert.IsTrue(Damageable(p, crystal));
        }

        [TestMethod]
        public void TwinConductors_ThePressureHalvesTheReviveTime()
        {
            var (s, p) = Arrived(HarbingerElement.Lightning);
            var guards = Guards(p, Lightning);

            Fire(s, p, HarbingerScript.TimerPressure);
            Died(s, p, Lightning.GuardTag, guards[1]);

            Assert.AreEqual(S.TwinReviveSeconds / 2, LastTimer(p, HarbingerScript.TimerRevivePrefix + 1), 1e-9);
        }

        [TestMethod]
        public void TwinConductors_OnlyInTheLightningAlcove()
        {
            foreach (var def in new[] { Acid, Frost, Fire_ })
            {
                var (s, p) = Arrived(def.Element);
                Died(s, p, def.GuardTag, Guards(p, def)[0]);
                Assert.IsFalse(p.TimerSets.Any(t => t.Tag.StartsWith(HarbingerScript.TimerRevivePrefix)), def.Tag);
            }
        }

        // ================================================================ reach: no safe ring

        /// <summary>Ember args with an override applied, as the runtime receives them (from the fake port).</summary>
        private static (HazardRegion Region, HazardArgs Args) EmberAsRun(HarbingerSettings settings, bool pressured)
        {
            var (s, p) = Arrived(HarbingerElement.Fire, settings);

            if (pressured)
                Fire(s, p, HarbingerScript.TimerPressure);

            var run = SigRuns(p).Last();
            return (Resolve(run), run.Args);
        }

        private static (HazardRegion Region, HazardArgs Args) WhiteoutAsRun(HarbingerSettings settings, bool pressured)
        {
            var (s, p) = Arrived(HarbingerElement.Frost, settings);

            if (pressured)
                Fire(s, p, HarbingerScript.TimerPressure);

            var run = SigRuns(p).Last();
            return (Resolve(run), run.Args);
        }

        [TestMethod]
        public void Whiteout_ReachesEveryFloorPoint_AtEveryBurstRadiusBound()
        {
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Frost);

            foreach (var burst in new[] { S.WhiteoutBurstRadius, 1.1, 3.0 })
            {
                foreach (var pressured in new[] { false, true })
                {
                    var (r, a) = WhiteoutAsRun(S with { WhiteoutBurstRadius = burst }, pressured);
                    var rs = RainSettings.From(a);
                    var missed = HarbingerSignatureReach.FloorModel(alcove).Where(pt => !HarbingerSignatureReach.WhiteoutHits(r, rs, pt)).ToList();

                    Assert.AreEqual(0, missed.Count, $"burst {burst}: {missed.Count} floor points out of reach, e.g. {string.Join("; ", missed.Take(5))}");
                    Assert.IsTrue((r.Radius - rs.EdgeInset) + rs.BurstRadius + rs.PlayerRadius >= HazardRegionPresets.HarbingerAlcovePlayerRadius - 1e-4,
                        "the bursts reach as far out as the region counts a player");
                    Assert.IsNull(RainRules.Validate(r, a, null), $"burst {burst} pressured {pressured}");
                }
            }

            // control: a burst under the 1.1 floor leaves the sill corners out of reach, so the check bites
            var (cr, ca) = WhiteoutAsRun(S, false);
            var small = RainSettings.From(HazardArgs.Parse(ca.ToString().Replace("burst_radius=1.2", "burst_radius=0.9")));
            Assert.AreEqual(0.9, small.BurstRadius, 1e-9);
            Assert.IsTrue(HarbingerSignatureReach.FloorModel(alcove).Any(pt => !HarbingerSignatureReach.WhiteoutHits(cr, small, pt)));
        }

        [TestMethod]
        public void EmberRoll_HitsEveryStandingPoint_NoSafeDoorwayLane()
        {
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Fire);

            foreach (var radius in new[] { S.EmberBallRadius, 0.3, 0.5 })
            {
                foreach (var pressured in new[] { false, true })
                {
                    var (r, a) = EmberAsRun(S with { EmberBallRadius = radius }, pressured);
                    var bp = BallRules.Read(a, BallPatterns.Random);

                    Assert.AreEqual(0, r.Exclusions.Count, "round 4: the fire alcove has no doorway lane exclusion");
                    Assert.AreEqual(BallPatterns.AxisAny, bp.Axis);
                    Assert.IsNull(BallRules.Validate(r, a, BallPatterns.Random), $"radius {radius} pressured {pressured}");

                    // where a player's centre can be (the library's 0.5 m player disc against every wall): every point, the
                    // old doorway lane included, is within reach of some lane
                    var standing = HarbingerSignatureReach.FloorModel(alcove, HarbingerSignatureReach.PlayerDisc).ToList();
                    var lanes = HarbingerSignatureReach.EmberLanes(r, bp);
                    var missed = standing.Where(pt => !HarbingerSignatureReach.EmberHits(lanes, bp.Reach, pt)).ToList();

                    Assert.IsTrue(standing.Count > 2500, $"{standing.Count} points");
                    Assert.AreEqual(0, missed.Count, $"radius {radius}: {missed.Count} standing points out of reach, e.g. {string.Join("; ", missed.Take(5))}");

                    // lanes come from all four sides: both axes are present
                    Assert.IsTrue(lanes.Any(l => Math.Abs(l.A.X - l.B.X) < 1e-4f), "north-south lanes");
                    Assert.IsTrue(lanes.Any(l => Math.Abs(l.A.Y - l.B.Y) < 1e-4f), "east-west lanes");

                    // and no ball body touches a wall: its centre stays within (radius - edge_inset), its body inside 4.5 m
                    Assert.IsTrue(r.Radius - bp.Inset + bp.BallRadius <= HazardRegionPresets.HarbingerAlcoveWallDistance - 0.05f, $"radius {radius}");
                }
            }
        }

        [TestMethod]
        public void Pools_TheSlotsCoverTheWholeFloorAndSill_AtEveryPoolSize()
        {
            foreach (var alcove in HarbingerData.Alcoves)
            {
                foreach (var size in new[] { S.PoolSize, HarbingerLayout.MinPoolSize, 3.0 })
                {
                    var missed = HarbingerSignatureReach.FloorModel(alcove).Where(pt => !HarbingerSignatureReach.PoolCovers(alcove, size, pt)).ToList();
                    Assert.AreEqual(0, missed.Count, $"{alcove.Name} size {size}: {missed.Count} uncovered, e.g. {string.Join("; ", missed.Take(5))}");
                }

                // every slot centre stands on the floor (the measured floor is every point within 4.25 m of the centre)
                for (var i = 0; i < HarbingerLayout.PoolSlotsUV.Count; i++)
                    Assert.IsTrue(Vector2.Distance(HarbingerLayout.PoolSlot(alcove, i), alcove.Centre) <= 4.25f, $"{alcove.Name} slot {i}");
            }

            // control: a smaller pool leaves floor uncovered, so the check bites
            var acid = HarbingerData.AlcoveOf(HarbingerElement.Acid);
            Assert.IsTrue(HarbingerSignatureReach.FloorModel(acid).Any(pt => !HarbingerSignatureReach.PoolCovers(acid, 2.0, pt)));
            Assert.AreEqual(HarbingerOverrides.PoolSlotCount, HarbingerLayout.PoolSlotsUV.Count, "pool_cap's ceiling is one pool per slot");
        }

        [TestMethod]
        public void EmberAxis_IsAny_AndTheFireAlcoveHasNoExclusion()
        {
            var r = HarbingerSignatureReach.Preset(HarbingerElement.Fire);

            Assert.AreEqual(0, r.Exclusions.Count);
            Assert.AreEqual(BallPatterns.AxisAny, S.EmberArgs(0.2, false).GetString("axis"));
            Assert.AreEqual(BallPatterns.AxisAny, S.EmberArgs(0.2, true).GetString("axis"));
        }

        [TestMethod]
        public void RegionPreset_OfEveryAlcove_IsItsOwnPreset_AtItsCentre()
        {
            foreach (var alcove in HarbingerData.Alcoves)
            {
                Assert.IsTrue(HazardRegionPresets.TryGet(HarbingerLayout.RegionPreset(alcove), 0, out var r), alcove.Name);
                Assert.AreEqual(alcove.Centre.X, r.CenterX, 1e-4, alcove.Name);
                Assert.AreEqual(alcove.Centre.Y, r.CenterY, 1e-4, alcove.Name);
            }
        }

        [TestMethod]
        public void Overrides_ReachTheRuntime()
        {
            Assert.IsTrue(HarbingerOverrides.TryCreateScript(S, HazardArgs.Parse("ember_ball_radius=0.3 ember_every_seconds=12 whiteout_max_falling=5 pool_size=2.6 drip_seconds=20 twin_revive_seconds=7 pressure_seconds=45"), out var script, out var error), error);

            var p = new FakePinnacleFightPort(1);
            script.OnStart(p);
            script.EnterPhase(4, p);
            p.Phase = 4;
            ToAlcove(script, p, HarbingerElement.Fire);
            Arrive(script, p, HarbingerElement.Fire);
            var ember = SigRuns(p).Single().Args;
            Assert.AreEqual(0.3, ember.GetDouble("ball_radius", 0, 0, 9), 1e-12);
            Assert.AreEqual(12, ember.GetDouble("wave_every", 0, 0, 99), 1e-12);
            Assert.AreEqual(45, LastTimer(p, HarbingerScript.TimerPressure), 1e-12);

            Assert.AreEqual(2.6, script.Settings.PoolSize, 1e-12);
            Assert.AreEqual(20, script.Settings.DripSeconds, 1e-12);
            Assert.AreEqual(7, script.Settings.TwinReviveSeconds, 1e-12);
            Assert.AreEqual(5, script.Settings.WhiteoutMaxFalling);
        }
    }
}
