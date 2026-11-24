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
    /// The Harbinger element loop (plan T9) against the recording fake port. The port applies nothing: a test drives the
    /// clock, the owner's position, the health floor and the hazard level itself and delivers every event by hand. Reads no
    /// PropertyManager value (the script takes its settings record; the clamp lives in the record's defaults).
    /// </summary>
    [TestClass]
    public class HarbingerScriptTests
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

        private static void Ended(HarbingerScript s, FakePinnacleFightPort p, string tag, HazardOutcome outcome)
            => s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.HazardEnded, Tag = tag, Outcome = outcome }, p);

        /// <summary>Kills the newest add spawned under <paramref name="tag"/> and delivers its AddDied.</summary>
        private static void Die(HarbingerScript s, FakePinnacleFightPort p, string tag)
        {
            var h = p.Spawns.Last(x => x.Tag == tag).Result;
            p.Kill(h);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = tag, Handle = h }, p);
        }

        private static PinnaclePoint Pt(float x, float y, float z) => new PinnaclePoint(0u, x, y, z);

        private static HazardArgs Args(FakePinnacleFightPort p, string tag) => p.Hazards.Last(h => h.Tag == tag).Args;

        /// <summary>Enters phase <paramref name="n"/> (what the core does after an AdvancePhase).</summary>
        private static void ToPhase(HarbingerScript s, FakePinnacleFightPort p, HarbingerElement e)
        {
            p.Phase = (int)e + 1;
            s.EnterPhase(p.Phase, p);
        }

        /// <summary>The threshold fires in the phase of <paramref name="e"/>: immune, the alcove is up.</summary>
        private static void ToAlcove(HarbingerScript s, FakePinnacleFightPort p, HarbingerElement e)
        {
            if (p.Phase != (int)e + 1)
                ToPhase(s, p, e);

            p.AtFloor.Add(p.Head);
            Fire(s, p, HarbingerScript.TimerPoll);
        }

        private static void ToLanding(HarbingerScript s, FakePinnacleFightPort p, HarbingerElement e = HarbingerElement.Acid)
        {
            ToAlcove(s, p, e);
            Die(s, p, HarbingerData.Element(e).CrystalTag);
        }

        // ================================================================ start and phase entry

        [TestMethod]
        public void Start_ReachEngageFloor80_AcidFromTheFloor()
        {
            var (s, p) = Started();

            Assert.AreEqual((float)HarbingerSettings.Defaults.MeleeReach, p.MeleeReach[p.Head]);
            CollectionAssert.Contains(p.Engaged, p.Head);
            Assert.IsTrue(p.Attackable[p.Head]);
            Assert.AreEqual(HarbingerLines.LineStart, p.Said[0]);
            Assert.AreEqual(0.8, p.Floors[p.Head]);
            Assert.IsFalse(p.Immune[p.Head]);
            Assert.AreEqual(-35.95, Args(p, "acid").GetDouble("start_z", 0, -100, 100), 1e-9);
            Assert.AreEqual(1, p.Hazards.Count, "only the phase's element runs");
            Assert.AreEqual(HazardMechanicRegistry.Fill, p.Hazards[0].Kind);
        }

        [TestMethod]
        public void EnterPhase_SetsTheFloorFromTheThresholdTable()
        {
            var (s, p) = Started();

            for (var n = 1; n <= 4; n++)
            {
                s.EnterPhase(n, p);
                Assert.AreEqual(new[] { 0.8, 0.6, 0.4, 0.2 }[n - 1], p.Floors[p.Head], $"phase {n}");
            }
        }

        [TestMethod]
        public void EnterPhase_StartsTheTimers()
        {
            var (s, p) = Started();

            Assert.IsTrue(p.TimerSets.Contains((HarbingerScript.TimerPoll, 1.0)));
            Assert.IsTrue(p.TimerSets.Contains((HarbingerScript.TimerSummon, S.SummonSeconds)));
            Assert.IsTrue(p.TimerSets.Contains((HarbingerScript.TimerCast, S.CastSeconds)));
        }

        [TestMethod]
        public void Threshold_ImmunePortalCrystalTwoGuards_HazardKeepsRunning()
        {
            var (s, p) = Started();
            p.AtFloor.Add(p.Head);
            Fire(s, p, "poll");

            Assert.IsTrue(p.Immune[p.Head]);
            Assert.AreEqual(1, p.Props.Count(x => x.Prop.Wcid == 1008264));
            Assert.AreEqual(1, p.Spawns.Count(x => x.Wcid == 1008251));
            Assert.AreEqual(2, p.Spawns.Count(x => x.Wcid == 1008255));
            Assert.IsTrue(p.Spawns.Where(x => x.Wcid == 1008251 || x.Wcid == 1008255).All(x => x.Tier == PinnacleTier.Elite));
            Assert.IsFalse(p.StoppedHazardTags.Contains("acid"));
            Assert.AreEqual(HarbingerLines.LineThresholdAcid, p.Said.Last());
        }

        [TestMethod]
        public void Threshold_IsTakenOnce()
        {
            var (s, p) = Started();
            p.AtFloor.Add(p.Head);
            Fire(s, p, "poll");
            Fire(s, p, "poll");

            Assert.AreEqual(1, p.Spawns.Count(x => x.Wcid == 1008251));
            Assert.AreEqual(1, p.Props.Count(x => x.Prop.Wcid == 1008264));
        }

        [TestMethod]
        public void NotAtTheFloor_NoAlcove()
        {
            var (s, p) = Started();
            Fire(s, p, "poll");

            Assert.AreEqual(0, p.Spawns.Count);
            Assert.AreEqual(0, p.Props.Count(x => !x.Tag.StartsWith(HarbingerScript.TagWallPrefix)), "no alcove prop (the wall strips are the acid's)");
            Assert.IsFalse(p.Immune[p.Head]);
        }

        [TestMethod]
        public void Alcove_FirstArrival_EngagesTheCrystal_LeaveTauntOncePerPhase()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Acid);
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Acid);
            var crystal = p.Spawns.Last(x => x.Wcid == 1008251).Result;

            p.OwnerAt = Pt(0, -20, -36f);
            Fire(s, p, "poll");
            CollectionAssert.DoesNotContain(p.Engaged, crystal, "owner on the floor");

            p.OwnerAt = Pt(alcove.Centre.X + 6f, alcove.Centre.Y, 0f);
            Fire(s, p, "poll");
            CollectionAssert.DoesNotContain(p.Engaged, crystal, "z ok but more than 5.5 m from the centre");

            p.OwnerAt = Pt(alcove.Centre.X, alcove.Centre.Y, -3f);
            Fire(s, p, "poll");
            CollectionAssert.DoesNotContain(p.Engaged, crystal, "z below -2");

            p.OwnerAt = Pt(alcove.Centre.X + 1f, alcove.Centre.Y, 0f);
            Fire(s, p, "poll");
            Fire(s, p, "poll");

            Assert.AreEqual(1, p.Engaged.Count(h => h == crystal));
            Assert.AreEqual(1, p.Said.Count(l => l == HarbingerLines.LineAlcove));
        }

        // ================================================================ the crystal

        [TestMethod]
        public void CrystalDeath_StopsOnlyItsElement_KeepsImmunityUntilLanding()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Frost);
            var portal = p.Props.Last(x => x.Prop.Wcid == 1008266).Result;
            Die(s, p, "crystal-frost");

            CollectionAssert.AreEquivalent(new[] { "frost" }, p.StoppedHazardTags);
            CollectionAssert.AreEquivalent(new[] { "summon-frost", "guard-frost" }, p.DestroyedAddTags);
            CollectionAssert.Contains(p.DestroyedProps, portal);
            Assert.IsTrue(p.Immune[p.Head]);
            Assert.AreEqual(0, p.AdvanceCount);
            Assert.AreEqual(HarbingerLines.LineCrystalFrost, p.Said.Last());
        }

        [TestMethod]
        public void AcidCrystal_DrainsThenStops()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Acid);
            p.HazardLevel["acid"] = -30.0;
            Die(s, p, "crystal-acid");

            Assert.AreEqual(("acid", "rate", -2.0), p.Controls.Last());
            Assert.IsFalse(p.StoppedHazardTags.Contains("acid"), "still draining");

            var drain = p.TimerSets.Last(t => t.Tag == HarbingerScript.TimerDrainDone).Seconds;
            Assert.AreEqual((-30.0 - -35.95) / 2.0 + 1.0, drain, 1e-9);

            Fire(s, p, "drain-done");
            Assert.AreEqual("acid", p.StoppedHazardTags.Last());
            Assert.IsTrue(p.Immune[p.Head]);
        }

        [TestMethod]
        public void AcidCrystal_NoLiveRun_StopsAtOnce()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Acid);
            p.ControllableTags = new HashSet<string>();
            Die(s, p, "crystal-acid");

            Assert.AreEqual("acid", p.StoppedHazardTags.Last());
        }

        [TestMethod]
        public void CrystalSpawnFailed_StillEndsTheElement()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Lightning);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = "crystal-lightning", SpawnFailed = true }, p);

            CollectionAssert.AreEquivalent(new[] { "lightning" }, p.StoppedHazardTags);
        }

        [TestMethod]
        public void CrystalDeath_OutsideTheAlcove_IsIgnored()
        {
            var (s, p) = Started();
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = "crystal-acid", Handle = new PinnacleHandle(900) }, p);

            Assert.AreEqual(0, p.StoppedHazardTags.Count);
            Assert.AreEqual(0, p.DestroyedAddTags.Count);
        }

        // ================================================================ landing

        [TestMethod]
        public void Landing_RequiresGroundedOnTheChamberFloor()
        {
            var (s, p) = Started();
            ToLanding(s, p);

            p.OwnerGrounded = false;
            p.OwnerAt = Pt(10, -20, -20);
            Fire(s, p, "poll");
            p.OwnerGrounded = true;
            p.OwnerAt = Pt(0, -20, 0);
            Fire(s, p, "poll");
            Assert.AreEqual(0, p.AdvanceCount);

            p.OwnerAt = Pt(10, -20, -36f);
            Fire(s, p, "poll");
            Assert.AreEqual(1, p.AdvanceCount);
        }

        [TestMethod]
        public void Landing_AdvancesOnce_AndNeverBeforeTheCrystalDies()
        {
            var (s, p) = Started();
            p.OwnerGrounded = true;
            p.OwnerAt = Pt(10, -20, -36f);
            Fire(s, p, "poll");
            Assert.AreEqual(0, p.AdvanceCount, "Fighting");

            ToAlcove(s, p, HarbingerElement.Acid);
            Fire(s, p, "poll");
            Assert.AreEqual(0, p.AdvanceCount, "Alcove");

            Die(s, p, "crystal-acid");
            Fire(s, p, "poll");
            Fire(s, p, "poll");
            Assert.AreEqual(1, p.AdvanceCount);
        }

        // ================================================================ casts

        [TestMethod]
        public void Cast_SkippedWhileOwnerAboveMinus5_VulnFirstThenEveryFifth()
        {
            var (s, p) = Started();
            var def = HarbingerData.Element(HarbingerElement.Acid);

            p.OwnerAt = Pt(10, -20, 0f);
            Fire(s, p, HarbingerScript.TimerCast);
            Assert.AreEqual(0, p.NowCasts.Count);
            Assert.IsTrue(p.TimerSets.Count(t => t.Tag == HarbingerScript.TimerCast) >= 2, "re-armed even when skipped");

            p.OwnerAt = Pt(10, -20, -36f);

            for (var i = 0; i < 11; i++)
                Fire(s, p, HarbingerScript.TimerCast);

            Assert.AreEqual(11, p.NowCasts.Count);
            Assert.IsTrue(p.NowCasts.All(c => c.Caster == p.Head));
            Assert.AreEqual(4473u, p.NowCasts[0].SpellId);
            Assert.AreEqual(4473u, p.NowCasts[5].SpellId);
            Assert.AreEqual(4473u, p.NowCasts[10].SpellId);

            foreach (var i in new[] { 1, 2, 3, 4, 6, 7, 8, 9 })
                CollectionAssert.Contains(def.WarSpells.ToList(), p.NowCasts[i].SpellId, $"cast {i + 1}");
        }

        [TestMethod]
        public void Cast_NoOwnerPosition_IsSkipped()
        {
            var (s, p) = Started();
            Fire(s, p, HarbingerScript.TimerCast);

            Assert.AreEqual(0, p.NowCasts.Count);
        }

        [TestMethod]
        public void Cast_UsesTheCurrentElementsSpells()
        {
            var (s, p) = Started();
            ToPhase(s, p, HarbingerElement.Fire);
            p.OwnerAt = Pt(10, -20, -36f);
            Fire(s, p, HarbingerScript.TimerCast);

            Assert.AreEqual(4481u, p.NowCasts[0].SpellId);
        }

        [TestMethod]
        public void Cast_Final_PicksAnyElementWithTheRng()
        {
            var (s, p) = Started();
            s.EnterPhase(5, p);
            p.OwnerAt = Pt(10, -20, -36f);

            var vulns = HarbingerData.Elements.Select(e => e.VulnSpell).ToList();
            var seen = new HashSet<uint>();

            for (var i = 0; i < 80; i++)
                Fire(s, p, HarbingerScript.TimerCast);

            foreach (var c in p.NowCasts)
                seen.Add(c.SpellId);

            var all = HarbingerData.Elements.SelectMany(e => e.WarSpells.Concat(new[] { e.VulnSpell })).ToHashSet();
            Assert.IsTrue(seen.IsSubsetOf(all));
            Assert.IsTrue(seen.Count(x => !vulns.Contains(x)) >= 6, "war spells of more than one element");
        }

        // ================================================================ summons

        [TestMethod]
        public void Summons_CapTwo_NeverWithin5mOfTheOwner()
        {
            var (s, p) = Started();
            p.OwnerAt = Pt(28.5f, -28.5f, -36f);

            for (var i = 0; i < 5; i++)
                Fire(s, p, HarbingerScript.TimerSummon);

            var spawns = p.Spawns.Where(x => x.Tag == "summon-acid").ToList();
            Assert.AreEqual(2, spawns.Count);
            Assert.IsTrue(spawns.All(x => x.Wcid == 1008259 && x.Tier == PinnacleTier.Elite));
            Assert.IsTrue(spawns.All(x => Math.Sqrt(Math.Pow(x.At.X - 28.5, 2) + Math.Pow(x.At.Y + 28.5, 2)) >= 5.0));
            Assert.IsTrue(spawns.All(x => p.Engaged.Contains(x.Result)));
            Assert.IsTrue(spawns.All(x => HarbingerData.SummonPoints.Contains(x.At)));
        }

        [TestMethod]
        public void Summons_RefillWhenOneDies()
        {
            var (s, p) = Started();
            p.OwnerAt = Pt(0, 0, -36f);

            for (var i = 0; i < 3; i++)
                Fire(s, p, HarbingerScript.TimerSummon);

            p.Kill(p.Spawns.First(x => x.Tag == "summon-acid").Result);
            Fire(s, p, HarbingerScript.TimerSummon);

            Assert.AreEqual(3, p.Spawns.Count(x => x.Tag == "summon-acid"));
        }

        [TestMethod]
        public void Summons_NoneInTheLandingStage()
        {
            var (s, p) = Started();
            ToLanding(s, p);
            p.ClearRecordings();
            Fire(s, p, HarbingerScript.TimerSummon);

            Assert.AreEqual(0, p.Spawns.Count);
        }

        [TestMethod]
        public void Summons_Final_OneOfEachKind_CapFour()
        {
            var (s, p) = Started();
            p.ClearRecordings();
            s.EnterPhase(5, p);

            CollectionAssert.AreEquivalent(new uint[] { 1008259, 1008260, 1008261, 1008262 }, p.Spawns.Select(x => x.Wcid).ToList());

            Fire(s, p, HarbingerScript.TimerSummon);
            Assert.AreEqual(4, p.Spawns.Count, "all four alive: nothing more");

            p.Kill(p.Spawns.First(x => x.Tag == "summon-frost").Result);
            Fire(s, p, HarbingerScript.TimerSummon);
            Assert.AreEqual(5, p.Spawns.Count);
            Assert.AreEqual(2, p.Spawns.Count(x => x.Tag == "summon-frost"));
        }

        // ================================================================ hazards

        [TestMethod]
        public void Frost_OneFloorRun_CarriesTheSlow()
        {
            var (s, p) = Started();
            p.ClearRecordings();
            ToPhase(s, p, HarbingerElement.Frost);

            Assert.AreEqual(1, p.Hazards.Count);
            Assert.AreEqual("frost", p.Hazards[0].Tag);
            Assert.AreEqual(HazardMechanicRegistry.Rain, p.Hazards[0].Kind);
            Assert.AreEqual(S.HailSlow, p.Hazards[0].Options.HitSlow);
        }

        [TestMethod]
        public void Fire_RunsFromTheHead_Lightning_RunsMixed()
        {
            var (s, p) = Started();
            p.ClearRecordings();
            ToPhase(s, p, HarbingerElement.Fire);
            Assert.AreEqual("fire", p.Hazards[0].Tag);
            Assert.AreEqual(HazardMechanicRegistry.Balls, p.Hazards[0].Kind);
            Assert.AreEqual(p.Head, p.Hazards[0].Origin);
            Assert.IsNull(p.Hazards[0].Options);

            p.ClearRecordings();
            ToPhase(s, p, HarbingerElement.Lightning);
            Assert.AreEqual("lightning", p.Hazards[0].Tag);
            Assert.AreEqual(HazardMechanicRegistry.Wisps, p.Hazards[0].Kind);
            Assert.AreEqual("mixed", p.Hazards[0].Pattern);
        }

        [TestMethod]
        public void EveryRun_PassesItsMechanicsValidate_OnTheHarbingerRegion()
        {
            Assert.IsTrue(HazardRegionPresets.TryGet("harbinger", 0, out var region));
            var (s, p) = Started();
            p.ClearRecordings();
            s.EnterPhase(5, p);

            foreach (var h in p.Hazards)
            {
                var error = h.Kind switch
                {
                    HazardMechanicRegistry.Fill => FillRules.Validate(region, h.Args),
                    HazardMechanicRegistry.Wisps => new WispSquadMechanic().Validate(region, h.Args, h.Pattern),
                    HazardMechanicRegistry.Rain => new RainMechanic().Validate(region, h.Args, h.Pattern),
                    HazardMechanicRegistry.Balls => new RollingBallMechanic().Validate(region, h.Args, h.Pattern),
                    _ => "unknown kind",
                };

                Assert.IsNull(error, $"{h.Tag}: {error}");
            }
        }

        [TestMethod]
        public void Final_AllFourRuns_Halved_NoThreshold()
        {
            var (s, p) = Started();
            p.ClearRecordings();
            s.EnterPhase(5, p);

            CollectionAssert.AreEquivalent(new[] { "acid", "lightning", "frost", "fire" }, p.Hazards.Select(h => h.Tag).ToList());
            Assert.IsNull(p.Floors[p.Head]);
            Assert.IsFalse(p.Immune[p.Head]);
            Assert.AreEqual(HarbingerLines.LineFinal, p.Said.Last());

            var acid = Args(p, "acid");
            Assert.AreEqual(-33.5, acid.GetDouble("max_z", 0, -100, 100), 1e-9);
            Assert.AreEqual(-35.95, acid.GetDouble("start_z", 0, -100, 100), 1e-9);
            Assert.AreEqual(S.FinalAcidRate, acid.GetDouble("rate", 0, -5, 5), 1e-9);

            // the builders apply FinalScale themselves: the phase pct is passed un-halved, so the run carries exactly half of it
            var phasePct = HazardLevelScale.Scaled(p.Level, S.WispPct, S.HazardPctMin, S.HazardPctMax);
            Assert.AreEqual(phasePct * S.FinalScale, Args(p, "lightning").GetDouble("pct", 0, 0, 10), 1e-9);

            var firePct = HazardLevelScale.Scaled(p.Level, S.FirePct, S.HazardPctMin, S.HazardPctMax);
            Assert.AreEqual(firePct * S.FinalScale, Args(p, "fire").GetDouble("pct", 0, 0, 10), 1e-9);
            Assert.AreEqual(S.HailSlow, p.Hazards.Single(h => h.Tag == "frost").Options.HitSlow);
        }

        [TestMethod]
        public void Acid_DamageScale_IsTheLevelScaleTimesTheAcidFactor()
        {
            var (s, p) = Started();
            var levelScale = HazardLevelScale.BenchmarkPct(p.Level, S.HazardPctMin, S.HazardPctMax) / HazardLevelScale.AnchorPct;

            Assert.AreEqual(levelScale * S.AcidDamageScale, Args(p, "acid").GetDouble("damage_scale", 0, 0, 100), 1e-9);
        }

        [TestMethod]
        public void Acid_Rate_DoublesEverySixtySeconds_ToTheCap()
        {
            var (s, p) = Started();
            p.HazardLevel["acid"] = -30.0;

            p.Now = 59;
            Fire(s, p, "poll");
            Assert.AreEqual(0, p.Controls.Count(c => c.Key == "rate"));

            p.Now = 61;
            Fire(s, p, "poll");
            Assert.AreEqual(("acid", "rate", 0.04), p.Controls.Last(c => c.Key == "rate"));

            p.Now = 10000;
            Fire(s, p, "poll");
            Assert.AreEqual(("acid", "rate", S.AcidRateCap), p.Controls.Last(c => c.Key == "rate"));
        }

        [TestMethod]
        public void HazardEnded_Completed_Restarts_Aborted_AtMostFive()
        {
            var (s, p) = Started();
            ToPhase(s, p, HarbingerElement.Lightning);
            p.ClearRecordings();

            for (var i = 0; i < 7; i++)
                Ended(s, p, "lightning", HazardOutcome.Completed);

            Assert.AreEqual(7, p.Hazards.Count(h => h.Tag == "lightning"));

            p.ClearRecordings();

            for (var i = 0; i < 7; i++)
                Ended(s, p, "lightning", HazardOutcome.Aborted);

            Assert.AreEqual(5, p.Hazards.Count(h => h.Tag == "lightning"));
        }

        [TestMethod]
        public void HazardEnded_ForAnElementThatIsOver_IsIgnored()
        {
            var (s, p) = Started();
            ToLanding(s, p, HarbingerElement.Frost);
            p.ClearRecordings();
            Ended(s, p, "frost", HazardOutcome.Completed);
            Ended(s, p, "acid", HazardOutcome.Completed);

            Assert.AreEqual(0, p.Hazards.Count);
        }

        [TestMethod]
        public void HazardEnded_Retries_ResetEachPhase()
        {
            var (s, p) = Started();

            for (var i = 0; i < 7; i++)
                Ended(s, p, "acid", HazardOutcome.Aborted);

            ToPhase(s, p, HarbingerElement.Lightning);
            p.ClearRecordings();

            for (var i = 0; i < 7; i++)
                Ended(s, p, "lightning", HazardOutcome.Aborted);

            Assert.AreEqual(5, p.Hazards.Count);
        }

        [TestMethod]
        public void AcidRestart_CarriesTheLevel_NeverAboveTheRunsMaxZ()
        {
            var (s, p) = Started();
            p.HazardLevel["acid"] = -20.0;
            Fire(s, p, "poll");
            Ended(s, p, "acid", HazardOutcome.Completed);

            Assert.AreEqual(-20.0, Args(p, "acid").GetDouble("start_z", 0, -100, 100), 1e-9, "carried");

            // the final run's max_z is -33.5: a level above it is clamped, or FillRules.Validate refuses the start
            s.EnterPhase(5, p);
            p.HazardLevel["acid"] = -20.0;
            Fire(s, p, "poll");
            p.ClearRecordings();
            Ended(s, p, "acid", HazardOutcome.Completed);

            var args = Args(p, "acid");
            Assert.AreEqual(-33.5, args.GetDouble("start_z", 0, -100, 100), 1e-9);
            Assert.IsTrue(HazardRegionPresets.TryGet("harbinger", 0, out var region));
            Assert.IsNull(FillRules.Validate(region, args));
        }

        [TestMethod]
        public void AcidRestart_NeverBelowTheFloor()
        {
            var (s, p) = Started();
            p.HazardLevel["acid"] = -50.0;
            Fire(s, p, "poll");
            Ended(s, p, "acid", HazardOutcome.Completed);

            Assert.AreEqual(-35.95, Args(p, "acid").GetDouble("start_z", 0, -100, 100), 1e-9);
        }

        // ================================================================ win, restart, overrides

        [TestMethod]
        public void HeadDeath_SaysTheDeathLine_AndWins()
        {
            var (s, p) = Started();
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = PinnacleEvent.HeadTag, Handle = p.Head }, p);

            Assert.AreEqual(HarbingerLines.LineDeath, p.Said.Last());
            Assert.AreEqual(1, p.WinCount);
        }

        [TestMethod]
        public void RestartPhase_Fighting_RestartsTheRun_OnceEach()
        {
            var (s, p) = Started();
            p.ClearRecordings();
            s.RestartPhase(p);
            s.RestartPhase(p);

            Assert.AreEqual(2, p.Hazards.Count(h => h.Tag == "acid"), "one per restart, never a second concurrent start inside one");
            Assert.IsFalse(p.Immune[p.Head]);
        }

        [TestMethod]
        public void RestartPhase_Acid_StartsAtThePhasesStartingHeightAndRate()
        {
            var (s, p) = Started();
            p.HazardLevel["acid"] = -20.0;
            p.Now = 130;
            Fire(s, p, "poll");
            Assert.AreEqual(0.08, p.Controls.Last(c => c.Key == "rate").Value, 1e-9, "escalated by now");

            s.RestartPhase(p);

            var args = Args(p, "acid");
            Assert.AreEqual(-35.95, args.GetDouble("start_z", 0, -100, 100), 1e-9);
            Assert.AreEqual(0.02, args.GetDouble("rate", 0, -5, 5), 1e-9);
        }

        [TestMethod]
        public void Threshold_PortalFailedToSpawn_SkipsTheAlcove_AndLandingStillAdvances()
        {
            var (s, p) = Started();
            p.AtFloor.Add(p.Head);
            p.FailNextSpawns = 1;
            Fire(s, p, "poll");

            Assert.AreEqual(HarbingerStage.Landing, s.Stage);
            Assert.IsTrue(p.Immune[p.Head]);
            CollectionAssert.Contains(p.DestroyedAddTags, "crystal-acid", "a crystal that did spawn is not left standing");

            p.OwnerGrounded = true;
            p.OwnerAt = Pt(10, -20, -36f);
            Fire(s, p, "poll");
            Fire(s, p, "poll");
            Assert.AreEqual(1, p.AdvanceCount);
        }

        [TestMethod]
        public void EnterPhase_FromTheAlcove_DestroysThePortalProp()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Acid);
            var portal = p.Props.Last(x => x.Prop.Wcid == 1008264).Result;

            s.EnterPhase(3, p);

            CollectionAssert.Contains(p.DestroyedProps, portal);
        }

        [TestMethod]
        public void RestartPhase_Alcove_EngagesTheCrystalAgainOnArrival()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Acid);
            var alcove = HarbingerData.AlcoveOf(HarbingerElement.Acid);
            var crystal = p.Spawns.Last(x => x.Wcid == 1008251).Result;
            p.OwnerAt = Pt(alcove.Centre.X, alcove.Centre.Y, 0f);
            Fire(s, p, "poll");

            s.RestartPhase(p);
            var respawned = p.Spawns.Last(x => x.Wcid == 1008251).Result;
            Assert.AreNotEqual(crystal, respawned, "the restart respawns the crystal");
            Fire(s, p, "poll");

            Assert.AreEqual(1, p.Engaged.Count(h => h == crystal));
            Assert.AreEqual(1, p.Engaged.Count(h => h == respawned));
        }

        [TestMethod]
        public void AcidFloor_IsOneSharedValue()
        {
            Assert.AreEqual(HarbingerSettings.AcidFloorZ, HarbingerScript.AcidFloorZ);
            Assert.AreEqual(HarbingerSettings.AcidFloorZ, S.AcidArgs(-30, 0.02, 1.0, false).GetDouble("floor_z", 0, -100, 100), 1e-9);
        }

        [TestMethod]
        public void RestartPhase_Landing_StartsNothing_KeepsImmunity()
        {
            var (s, p) = Started();
            ToLanding(s, p, HarbingerElement.Acid);
            p.ClearRecordings();
            s.RestartPhase(p);

            Assert.AreEqual(0, p.Hazards.Count);
            Assert.IsTrue(p.Immune[p.Head]);
            Assert.IsTrue(p.CancelledTimers.Contains(HarbingerScript.TimerDrainDone));
        }

        [TestMethod]
        public void RestartPhase_Final_RestartsAllFour()
        {
            var (s, p) = Started();
            s.EnterPhase(5, p);
            p.ClearRecordings();
            s.RestartPhase(p);

            CollectionAssert.AreEquivalent(new[] { "acid", "lightning", "frost", "fire" }, p.Hazards.Select(h => h.Tag).ToList());
        }

        [TestMethod]
        public void Overrides_TryCreateScript_AppliesAndRefuses()
        {
            Assert.IsTrue(HarbingerOverrides.TryCreateScript(S, HazardArgs.Parse("summon_cap=3 cast_seconds=5"), out var script, out var error), error);
            Assert.AreEqual(3, script.Settings.SummonCap);
            Assert.AreEqual(5.0, script.Settings.CastSeconds);
            Assert.AreEqual(HarbingerData.Kind, script.Kind);
            Assert.AreEqual(5, script.PhaseCount);

            var p = new FakePinnacleFightPort();
            script.OnStart(p);
            script.EnterPhase(1, p);
            Assert.IsTrue(p.TimerSets.Contains((HarbingerScript.TimerCast, 5.0)), "the per-copy value reaches the fight");

            Assert.IsFalse(HarbingerOverrides.TryCreateScript(S, HazardArgs.Parse("nonsense=1"), out var none, out error));
            Assert.IsNull(none);
            StringAssert.Contains(error, "nonsense");

            Assert.IsTrue(HarbingerOverrides.TryCreateScript(S, HazardArgs.Empty, out var plain, out error), error);
            Assert.AreSame(S, plain.Settings);
        }

        [TestMethod]
        public void ScriptData_IsTheHarbingerData()
        {
            var s = new HarbingerScript();

            Assert.AreEqual(HarbingerData.HeadWcid, s.Data.HeadWcid);
            Assert.IsTrue(s.Data.WaiveFallDamage);
            Assert.AreEqual(HarbingerData.Kind, s.Kind);
            Assert.IsFalse(string.IsNullOrEmpty(s.Describe()));
        }

        // ================================================================ one code path

        // ================================================================ the wall strips (round 2 A2)

        /// <summary>Every wall strip prop the port holds alive now.</summary>
        private static List<FakePinnacleFightPort.PropCall> LiveStrips(FakePinnacleFightPort p)
            => p.Props.Where(x => x.Tag.StartsWith(HarbingerScript.TagWallPrefix) && p.IsAlive(x.Result)).ToList();

        /// <summary>The four strips at the shipped settings: per alcove, 11 flow panels and the acid fog's two hosts.</summary>
        private static readonly int StripProps = HarbingerData.Alcoves.Sum(a => HarbingerLayout.WallStrip(a, S.WallFx, S.FogAcid).Count);

        [TestMethod]
        public void WallStrips_StandAfterPhase1Enters_FourAlcoves_ExactlyTheLayoutsProps()
        {
            var (s, p) = Started();

            Assert.AreEqual(4 * (11 + 2), StripProps, "control: 11 panel rows and 2 fog hosts per wall");

            var live = LiveStrips(p);
            Assert.AreEqual(StripProps, live.Count, "every strip prop stands while the acid runs");

            foreach (var a in HarbingerData.Alcoves)
            {
                var expected = HarbingerLayout.WallStrip(a, S.WallFx, S.FogAcid);
                var got = live.Where(x => x.Tag.StartsWith($"{HarbingerScript.TagWallPrefix}{a.Name}-")).Select(x => x.Prop).ToList();
                CollectionAssert.AreEqual(expected.ToList(), got, $"the {a.Name} wall is the layout's strip, in order");
            }

            Assert.AreEqual(44, live.Count(x => x.Prop.Wcid == ACE.Server.Pinnacles.Matron.MatronData.AcidFlowWcid), "the Matron's acid flow panel");
        }

        [TestMethod]
        public void WallStrips_StayThroughTheDrain_GoWhenTheDrainIsDone()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Acid);
            p.HazardLevel["acid"] = -30.0;
            var handles = LiveStrips(p).Select(x => x.Result).ToList();

            Die(s, p, "crystal-acid");
            Assert.AreEqual(StripProps, LiveStrips(p).Count, "the acid is draining: the strips still flow");

            Fire(s, p, HarbingerScript.TimerDrainDone);
            Assert.AreEqual(0, LiveStrips(p).Count, "the drain is done: every strip is gone");
            CollectionAssert.IsSubsetOf(handles, p.DestroyedProps, "destroyed by the script, not left to the fight's end");

            // the next phase brings none back
            ToPhase(s, p, HarbingerElement.Lightning);
            Assert.AreEqual(0, LiveStrips(p).Count, "no strip in a lightning phase");
        }

        [TestMethod]
        public void WallStrips_ADrainThatCannotStart_TakesThemAtOnce()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Acid);
            p.ControllableTags = new HashSet<string>();
            Die(s, p, "crystal-acid");

            Assert.AreEqual("acid", p.StoppedHazardTags.Last(), "control: the run stopped at once");
            Assert.AreEqual(0, LiveStrips(p).Count);
        }

        [TestMethod]
        public void WallStrips_RestartPhase_NeverDoublesThem_AndRespawnsOnlyALostOne()
        {
            var (s, p) = Started();
            var before = p.Props.Count(x => x.Tag.StartsWith(HarbingerScript.TagWallPrefix));

            s.RestartPhase(p);
            ToAlcove(s, p, HarbingerElement.Acid);
            s.RestartPhase(p);

            Assert.AreEqual(before, p.Props.Count(x => x.Tag.StartsWith(HarbingerScript.TagWallPrefix)), "two restarts spawned no strip prop");
            Assert.AreEqual(StripProps, LiveStrips(p).Count);

            // a strip prop lost (killed outside the script) comes back alone at the next restart
            var lost = LiveStrips(p)[5];
            p.Kill(lost.Result);
            var mark = p.Props.Count;
            s.RestartPhase(p);

            var respawned = p.Props.Skip(mark).Where(x => x.Tag.StartsWith(HarbingerScript.TagWallPrefix)).ToList();
            Assert.AreEqual(1, respawned.Count, "only the lost one");
            Assert.AreEqual(lost.Tag, respawned[0].Tag);
            Assert.AreEqual(lost.Prop, respawned[0].Prop);
            Assert.AreEqual(StripProps, LiveStrips(p).Count);
        }

        [TestMethod]
        public void WallStrips_RestartDuringTheDrain_TakesThem_TheDrainIsOver()
        {
            var (s, p) = Started();
            ToAlcove(s, p, HarbingerElement.Acid);
            p.HazardLevel["acid"] = -30.0;
            Die(s, p, "crystal-acid");
            Assert.AreEqual(StripProps, LiveStrips(p).Count, "control: draining");

            s.RestartPhase(p);

            Assert.AreEqual(0, LiveStrips(p).Count, "the hold ended the drain, so the strips go with it");
        }

        [TestMethod]
        public void WallStrips_StandInTheFinal_AndARestartDoesNotDoubleThem()
        {
            var (s, p) = Started();
            ToPhase(s, p, HarbingerElement.Fire);
            Assert.AreEqual(0, LiveStrips(p).Count, "control: none in the fire phase");

            s.EnterPhase(5, p);
            Assert.AreEqual(StripProps, LiveStrips(p).Count, "the final's acid runs: the strips stand");

            var count = p.Props.Count;
            s.RestartPhase(p);
            Assert.AreEqual(count, p.Props.Count, "a final restart spawns no prop");
            Assert.AreEqual(StripProps, LiveStrips(p).Count);
        }

        [TestMethod]
        public void WallStrips_AreDestroyedWhenTheFinalIsWon()
        {
            var (s, p) = Started();
            s.EnterPhase(5, p);
            var handles = LiveStrips(p).Select(x => x.Result).ToList();
            Assert.AreEqual(StripProps, handles.Count, "control: the final's strips stand");

            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = PinnacleEvent.HeadTag, Handle = p.Head }, p);

            Assert.AreEqual(1, p.WinCount);
            Assert.AreEqual(0, LiveStrips(p).Count, "a Win leaves props standing (IPinnacleFightPort.SpawnProp), so the script takes them");
            CollectionAssert.IsSubsetOf(handles, p.DestroyedProps);
        }

        [TestMethod]
        public void WallStrips_GoWhenTheAcidRunIsAbandoned_AndComeBackWithItsRestart()
        {
            var (s, p) = Started();

            for (var i = 0; i < HarbingerScript.MaxRunRestarts; i++)
            {
                Ended(s, p, "acid", HazardOutcome.Aborted);
                Assert.AreEqual(StripProps, LiveStrips(p).Count, $"retry {i + 1}: the run restarted, the strips stand");
            }

            var starts = p.Hazards.Count(h => h.Tag == "acid");
            Ended(s, p, "acid", HazardOutcome.Aborted);

            Assert.AreEqual(starts, p.Hazards.Count(h => h.Tag == "acid"), "control: the retries are spent, no new acid run");
            Assert.AreEqual(0, LiveStrips(p).Count, "no acid runs, so no acid flows down the walls");

            // a resume starts the acid again, and the strips with it
            s.RestartPhase(p);
            Assert.AreEqual(starts + 1, p.Hazards.Count(h => h.Tag == "acid"));
            Assert.AreEqual(StripProps, LiveStrips(p).Count);
        }

        [TestMethod]
        public void WallStrips_AnotherElementsAbandonedRun_LeavesThem()
        {
            var (s, p) = Started();
            s.EnterPhase(5, p);

            for (var i = 0; i <= HarbingerScript.MaxRunRestarts; i++)
                Ended(s, p, "lightning", HazardOutcome.Aborted);

            Assert.AreEqual(StripProps, LiveStrips(p).Count, "only the acid's end takes the strips");
        }

        [TestMethod]
        public void WallStrips_Overrides_WallFxAndFogAcid_ReachTheProps()
        {
            Assert.IsTrue(HarbingerOverrides.TryApply(S, HazardArgs.Parse("wall_fx=0"), out var panelsOnly, out var e), e);
            var (_, p) = Started(panelsOnly);
            Assert.AreEqual(44, LiveStrips(p).Count, "panels only");
            Assert.IsTrue(LiveStrips(p).All(x => x.Prop.Wcid == ACE.Server.Pinnacles.Matron.MatronData.AcidFlowWcid));

            Assert.IsTrue(HarbingerOverrides.TryApply(S, HazardArgs.Parse("wall_fx=1 fog_acid=1"), out var fogOne, out e), e);
            (_, p) = Started(fogOne);
            var live = LiveStrips(p);
            Assert.AreEqual(4, live.Count, "fog only, the alternate on one host per wall");
            Assert.IsTrue(live.All(x => x.Prop.Wcid == HazardDefaults.EffectHostWcid && x.Prop.Script == 0x33000A44u), "the fog_acid=1 script");

            Assert.IsFalse(HarbingerOverrides.TryApply(S, HazardArgs.Parse("wall_fx=3"), out _, out e));
            Assert.IsFalse(HarbingerOverrides.TryApply(S, HazardArgs.Parse("fog_acid=1.5"), out _, out e));
            StringAssert.Contains(e, "whole number");
        }

        /// <summary>The shape of one element's loop, with every element-keyed value replaced by a role.</summary>
        private static List<string> Shape(HarbingerElement e)
        {
            var def = HarbingerData.Element(e);
            var alcove = HarbingerData.AlcoveOf(e);
            var (s, p) = Started();
            var dump = new List<string>();

            // the acid's wall strips (round 2 A2) are acid-only props, spawned at phase 1's start: left out of the shape
            var strips = p.Props.Where(x => x.Tag.StartsWith(HarbingerScript.TagWallPrefix)).Select(x => x.Result).ToHashSet();

            string Tag(string tag) => tag.Replace(def.Tag, "<el>");

            string Role(uint wcid)
                => wcid == def.CrystalWcid ? "crystal"
                : wcid == def.GuardianWcid ? "guardian"
                : wcid == def.SummonWcid ? "summon"
                : wcid == alcove.PortalWcid ? "portal"
                : $"other:{wcid}";

            string Line(string said)
                => said == HarbingerLines.Threshold(e) ? "threshold"
                : said == HarbingerLines.Crystal(e) ? "crystal-line"
                : said == HarbingerLines.LineAlcove ? "alcove-line"
                : $"other:{said}";

            p.ClearRecordings();
            ToPhase(s, p, e);
            dump.Add("floor is the phase's threshold " + (p.Floors[p.Head] == HarbingerScript.Thresholds[(int)e]));
            dump.Add("runs " + string.Join(",", p.Hazards.Select(h => Tag(h.Tag))));
            dump.Add("timers " + string.Join(",", p.TimerSets.Select(t => t.Tag).Distinct().OrderBy(x => x)));

            p.OwnerAt = Pt(10, -20, -36f);
            Fire(s, p, HarbingerScript.TimerSummon);
            dump.Add("summon " + string.Join(",", p.Spawns.Select(x => $"{Tag(x.Tag)}={Role(x.Wcid)}/{x.Tier}")));

            p.ClearRecordings();
            p.AtFloor.Add(p.Head);
            Fire(s, p, "poll");
            dump.Add("threshold immune=" + p.Immune[p.Head]);
            dump.Add("spawns " + string.Join(",", p.Spawns.Select(x => $"{Tag(x.Tag)}={Role(x.Wcid)}/{x.Tier}")));
            dump.Add("props " + string.Join(",", p.Props.Where(x => !x.Tag.StartsWith(HarbingerScript.TagWallPrefix)).Select(x => $"{Tag(x.Tag)}={Role(x.Prop.Wcid)}")));
            dump.Add("said " + string.Join(",", p.Said.Select(Line)));
            dump.Add("stops " + string.Join(",", p.StoppedHazardTags.Select(Tag)));

            var crystal = p.Spawns.Last(x => x.Tag == def.CrystalTag).Result;
            p.OwnerAt = Pt(alcove.Centre.X, alcove.Centre.Y, 0f);
            p.ClearRecordings();
            Fire(s, p, "poll");
            dump.Add("arrival engaged=" + p.Engaged.Count + " said " + string.Join(",", p.Said.Select(Line)));

            p.ClearRecordings();
            p.Kill(crystal);
            s.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = def.CrystalTag, Handle = crystal }, p);

            // the acid's drain is its one element-keyed difference: it stops when the drain timer fires
            if (e == HarbingerElement.Acid)
                Fire(s, p, HarbingerScript.TimerDrainDone);

            // the alcove signature (round 2 B2) is element-keyed by design (a run for frost and fire, pools for acid, none for
            // lightning): its stops are left out of the shape here and pinned per element in HarbingerSignatureTests
            dump.Add("crystal stops " + string.Join(",", p.StoppedHazardTags.Where(t => !t.StartsWith(HarbingerScript.TagSignaturePrefix) && !t.StartsWith(HarbingerScript.TagPoolPrefix)).Select(Tag)));
            dump.Add("destroyed " + string.Join(",", p.DestroyedAddTags.Select(Tag)));
            dump.Add("props destroyed " + p.DestroyedProps.Count(h => !strips.Contains(h)));
            dump.Add("said " + string.Join(",", p.Said.Select(Line)));
            dump.Add("immune=" + p.Immune.GetValueOrDefault(p.Head, true) + " advance=" + p.AdvanceCount);

            p.ClearRecordings();
            p.OwnerGrounded = true;
            p.OwnerAt = Pt(10, -20, -36f);
            Fire(s, p, "poll");
            dump.Add("landing advance=" + p.AdvanceCount);

            p.ClearRecordings();
            Fire(s, p, HarbingerScript.TimerSummon);
            dump.Add("summon after crystal " + p.Spawns.Count);

            // spells: the owner is on the floor; six casts map to roles through the element's own def
            p.ClearRecordings();

            for (var i = 0; i < 6; i++)
                Fire(s, p, HarbingerScript.TimerCast);

            dump.Add("casts " + string.Join(",", p.NowCasts.Select(c => c.SpellId == def.VulnSpell ? "vuln" : def.WarSpells.Contains(c.SpellId) ? "war" : "other")));

            return dump;
        }

        [TestMethod]
        public void FourElements_OneCodePath()
        {
            var acid = Shape(HarbingerElement.Acid);

            foreach (var e in new[] { HarbingerElement.Lightning, HarbingerElement.Frost, HarbingerElement.Fire })
            {
                var other = Shape(e);
                CollectionAssert.AreEqual(acid, other, $"{e} differs from Acid in more than its element-keyed values:{Environment.NewLine}{string.Join(Environment.NewLine, acid)}{Environment.NewLine}--{Environment.NewLine}{string.Join(Environment.NewLine, other)}");
            }

            // the dump is not vacuous: it saw the crystal, two guardians, the portal and the stop
            CollectionAssert.Contains(acid, "spawns crystal-<el>=crystal/Elite,guard-<el>=guardian/Elite,guard-<el>=guardian/Elite");
            CollectionAssert.Contains(acid, "props portal-<el>=portal");
            CollectionAssert.Contains(acid, "crystal stops <el>");
            CollectionAssert.Contains(acid, "landing advance=1");
        }

        [TestMethod]
        public void ElementValues_AreTheirOwn()
        {
            var wcids = new List<uint>();

            foreach (var e in HarbingerData.Elements)
            {
                var (s, p) = Started();
                ToAlcove(s, p, e.Element);
                wcids.AddRange(p.Spawns.Where(x => x.Tag != e.SummonTag).Select(x => x.Wcid));
                Assert.IsTrue(p.Props.Any(x => x.Prop.Wcid == HarbingerData.AlcoveOf(e.Element).PortalWcid), e.Tag);
                Assert.IsTrue(p.Spawns.Any(x => x.Wcid == e.CrystalWcid && x.At == HarbingerData.AlcoveOf(e.Element).Crystal), e.Tag);
            }

            // a crystal and a guardian wcid per element (the two guards share one)
            Assert.AreEqual(8, wcids.Distinct().Count());
        }
    }
}
