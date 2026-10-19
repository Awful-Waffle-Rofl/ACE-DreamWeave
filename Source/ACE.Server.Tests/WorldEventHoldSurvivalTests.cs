using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Outcome = ACE.Server.WorldEvents.WorldEventHoldRules.ProtectedHealOutcome;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Keeping the targets alive (2026-10-18): corrupted-kill heals, shrines, rescues and clear regeneration, all
    /// through Creature.ApplyWorldEventProtectedHeal with the after-hit reduction and the per-wave healing budget;
    /// the smaller, slower, group-scaled waves. Shares the builders in WorldEventHoldTests.
    /// </summary>
    public partial class WorldEventHoldTests
    {
        private static (WorldEvent Evt, ProtectDef Protect, Creature Target) BuildBudgetedTarget(double budget = 0.25)
        {
            var (evt, protect) = BuildTimeScaledRun();
            protect.HealBudgetPerWave = budget;
            protect.RescueCapPerWave = 0.10;

            var target = BuildFreshTarget(evt);
            target.UpdateVitalDelta(target.Health, -6000);   // 40% health

            return (evt, protect, target);
        }

        // ---- the healing budget ---------------------------------------------------------------------------------

        [TestMethod]
        public void HealBudget_PureRule()
        {
            Assert.AreEqual(1500, WorldEventHoldRules.HealWithinBudget(1500, 10000, 0.25, 0));
            Assert.AreEqual(1000, WorldEventHoldRules.HealWithinBudget(1500, 10000, 0.25, 1500), "only what is left of 2500");
            Assert.AreEqual(0, WorldEventHoldRules.HealWithinBudget(500, 10000, 0.25, 2500), "spent");
            Assert.AreEqual(500, WorldEventHoldRules.HealWithinBudget(500, 10000, 0, 99999), "0 means no budget");
            Assert.AreEqual(0, WorldEventHoldRules.HealWithinBudget(-5, 10000, 0.25, 0));
        }

        [TestMethod]
        public void HealBudget_EverySourceTogether_ThenNothingUntilTheNextWave()
        {
            var (evt, _, target) = BuildBudgetedTarget();
            var now = T0 + 100;   // long after any hit
            var max = target.Health.MaxValue;
            int Pct(double share) => (int)Math.Floor(share * max);
            var start = target.Health.Current;

            Assert.AreEqual(Pct(0.15), target.ApplyWorldEventProtectedHeal(0.15, false, now, out var o1), "a shrine: 15%");
            Assert.AreEqual(Outcome.Healed, o1);

            Assert.AreEqual(Pct(0.25) - Pct(0.15), target.ApplyWorldEventProtectedHeal(0.10, false, now, out _), "a corrupted kill: what is left of 25%");
            Assert.AreEqual(0, target.ApplyWorldEventProtectedHeal(0.10, false, now, out var o3), "25% spent: nothing more this wave");
            Assert.AreEqual(Outcome.BudgetSpent, o3);
            Assert.AreEqual(Pct(0.25), target.WorldEventHealSpentThisWave);
            Assert.AreEqual(start + (uint)Pct(0.25), target.Health.Current);

            // The next wave starts: a fresh budget.
            AdvanceWave(evt);
            Assert.AreEqual(0, target.WorldEventHealSpentThisWave);
            Assert.AreEqual(Pct(0.15), target.ApplyWorldEventProtectedHeal(0.15, false, now, out _));
        }

        [TestMethod]
        public void HealBudget_AlsoHoldsPlayerHeals()
        {
            var (_, protect, target) = BuildBudgetedTarget();
            protect.MaxHealPerTenSeconds = 1.0;   // let the player cap through, so only the budget limits

            var max = target.Health.MaxValue;

            target.ApplyWorldEventProtectedHeal(0.20, false, T0 + 100, out _);
            Assert.AreEqual((int)Math.Floor(0.20 * max), target.WorldEventHealSpentThisWave);

            var player = target.ConvertWorldEventProtectedHeal(5000, T0 + 100);
            Assert.AreEqual((int)Math.Floor(0.25 * max) - (int)Math.Floor(0.20 * max), player, "a player's heal is held to what is left of the budget");
        }

        [TestMethod]
        public void HealReduction_AppliesToEverySource()
        {
            var (evt, _, target) = BuildBudgetedTarget(budget: 0);

            var monster = BuildHoldCreature();
            monster.P_WorldEvent = evt;
            target.ScaleWorldEventProtectedHit(monster, 30f, T0);
            var afterHit = target.Health.Current;

            var max = target.Health.MaxValue;
            var half = (int)Math.Floor(0.10 * max * 0.5);
            var whole = (int)Math.Floor(0.10 * max);

            Assert.AreEqual(half, target.ApplyWorldEventProtectedHeal(0.10, false, T0 + 2, out _), "half, 2 s after a hit");
            Assert.AreEqual(whole, target.ApplyWorldEventProtectedHeal(0.10, false, T0 + 6, out _), "full again after 5 s");
            Assert.AreEqual(afterHit + (uint)(half + whole), target.Health.Current);
        }

        [TestMethod]
        public void Rescue_CappedPerWave_AndInsideTheBudget()
        {
            var (evt, _, target) = BuildBudgetedTarget();
            var max = target.Health.MaxValue;

            var total = 0;

            for (var i = 0; i < 15; i++)
                total += target.ApplyWorldEventProtectedHeal(0.01, true, T0 + 100, out _);

            Assert.AreEqual((int)Math.Floor(0.10 * max), total, 1.0, "1% a rescue, at most 10% a wave");

            // A rescue cap that is spent leaves the rest of the budget for other sources.
            Assert.AreEqual((int)Math.Floor(0.15 * max), target.ApplyWorldEventProtectedHeal(0.15, false, T0 + 100, out _));

            AdvanceWave(evt);
            Assert.AreEqual((int)Math.Floor(0.01 * max), target.ApplyWorldEventProtectedHeal(0.01, true, T0 + 100, out _), "the rescue cap resets with the wave");
        }

        [TestMethod]
        public void ProtectedHeal_FullHealth_AndNonTargets()
        {
            var (evt, _, _) = BuildBudgetedTarget();

            var full = BuildFreshTarget(evt);
            Assert.AreEqual(0, full.ApplyWorldEventProtectedHeal(0.15, false, T0 + 100, out var o));
            Assert.AreEqual(Outcome.Full, o);
            Assert.AreEqual(0, full.WorldEventHealSpentThisWave, "a wasted heal spends nothing");

            var monster = BuildHoldCreature();
            monster.P_WorldEvent = evt;
            Assert.AreEqual(0, monster.ApplyWorldEventProtectedHeal(0.15, false, T0, out var none), "only a protected target");
            Assert.AreEqual(Outcome.None, none);
        }

        /// <summary>Starts a new wave on the run, as SpawnWave does, for the budget's wave counter.</summary>
        private static void AdvanceWave(WorldEvent evt)
        {
            var field = typeof(WorldEvent).GetField("holdWavesStarted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field.SetValue(evt, (int)field.GetValue(evt) + 1);
        }

        // ---- rescue, clear regeneration, shrines ------------------------------------------------------------

        [TestMethod]
        public void Rescue_OnlyAPlayersOrPetsKill()
        {
            Assert.IsTrue(WorldEventHoldRules.IsRescueKill(killerIsPlayer: true, killerHasPetOwner: false));
            Assert.IsTrue(WorldEventHoldRules.IsRescueKill(false, true), "a pet's kill");
            Assert.IsFalse(WorldEventHoldRules.IsRescueKill(false, false), "never a monster's");
        }

        [TestMethod]
        public void ClearRegen_StopsTheMomentAHostileIsNear()
        {
            Assert.IsTrue(WorldEventHoldRules.IsClear(null, 10), "no monster alive");
            Assert.IsTrue(WorldEventHoldRules.IsClear(10.1, 10));
            Assert.IsFalse(WorldEventHoldRules.IsClear(10, 10), "on the edge counts as near");
            Assert.IsFalse(WorldEventHoldRules.IsClear(2, 10));
        }

        [TestMethod]
        public void Shrine_Lines_AndReadyPulse()
        {
            var cooldown = WorldEventHoldRules.ShrineCooldownLine(23.2);
            Assert.AreEqual("The shrine is still gathering its strength. It will be ready in 24 seconds.", cooldown);
            Assert.AreEqual("The shrine is still gathering its strength. It will be ready in 1 second.", WorldEventHoldRules.ShrineCooldownLine(0.4));

            var wasted = WorldEventHoldRules.ShrineWastedLine("Sir Donovan", Outcome.BudgetSpent);
            Assert.AreEqual("Sir Donovan can draw no more strength this wave. The shrine holds its power for now.", wasted);
            StringAssert.StartsWith(WorldEventHoldRules.ShrineWastedLine("Sir Adarl", Outcome.Full), "Sir Adarl needs no restoring.");

            foreach (var line in new[] { cooldown, wasted })
            {
                AssertAscii(line);
                Assert.IsTrue(SentenceCount(line) <= 3);
            }

            Assert.IsTrue(WorldEventHoldRules.ShrineReadyPulseDue(T0, 0, 0, 8), "never used: ready, pulse");
            Assert.IsFalse(WorldEventHoldRules.ShrineReadyPulseDue(T0 + 3, 0, T0, 8), "pulsed 3 s ago");
            Assert.IsTrue(WorldEventHoldRules.ShrineReadyPulseDue(T0 + 8, 0, T0, 8));
            Assert.IsFalse(WorldEventHoldRules.ShrineReadyPulseDue(T0 + 10, T0 + 45, T0, 8), "resting: no ready effect");
            Assert.IsTrue(WorldEventHoldRules.ShrineReadyPulseDue(T0 + 45, T0 + 45, T0 + 40, 8), "the moment it is ready again");
        }

        [TestMethod]
        public void Shrine_DecorBesideEachTarget_UsableAndNamed()
        {
            var shrine = new ShrineDef();
            var decor = WorldEventHoldRules.ShrineDecor(shrine);

            Assert.AreEqual(2000008u, decor.Wcid, "the DreamWeave Restoration Shrine");
            Assert.AreEqual(1f, decor.Scale, "normal size");
            Assert.IsTrue(decor.HoldShrine);
            Assert.IsTrue(decor.Snap, "on the ground");

            var placed = WorldEventHoldRules.PlaceTargetDecor(WorldEventHoldRules.RingOffsets(3, 16), new List<DecorDef> { decor });
            Assert.AreEqual(3, placed.Count, "one per target");
            Assert.IsTrue(placed.All(p => p.HoldShrine), "the flag survives placement");

            var wo = BuildHoldCreature();
            WorldEventSpawner.ConfigureHoldShrine(wo, shrine);

            Assert.AreEqual("Restoration Shrine", wo.Name);
            Assert.AreEqual(Usable.Remote, wo.ItemUseable, "the client lets a player use it");
            Assert.AreEqual(4f, wo.UseRadius);
            Assert.IsNull(wo.GetProperty(ACE.Entity.Enum.Properties.PropertyString.ShortDesc), "no PK altar text on appraisal");
            Assert.IsNull(wo.GetProperty(ACE.Entity.Enum.Properties.PropertyString.UseMessage));
            StringAssert.Contains(wo.GetProperty(ACE.Entity.Enum.Properties.PropertyString.Use), "Double-click");
        }

        // ---- waves: smaller for few players, a breather between ----------------------------------------------

        [TestMethod]
        public void WaveMix_SmallWaves_KeepTheBreachSquadRangedAndCorrupted()
        {
            var mix = new WaveMixDef { MinBreach = 2 };

            int[] Counts(int size) => WorldEventHoldRules.WaveMixCounts(size, mix.Shares, mix.Minimums, mix.Maximums);

            // Solo (5): 2 breach, 2 ranged (3 would be most of the wave), 1 corrupted.
            CollectionAssert.AreEqual(new[] { 0, 2, 0, 2, 0, 1 }, Counts(5));

            foreach (var size in new[] { 5, 7, 10, 15, 27, 40 })
            {
                var c = Counts(size);
                Assert.AreEqual(size, c.Sum(), $"wave of {size}");
                Assert.IsTrue(c[(int)HoldRole.Breach] >= 2, $"wave of {size}: a breach squad, not one monster");
                Assert.IsTrue(c[(int)HoldRole.Corrupted] >= 1 && c[(int)HoldRole.Corrupted] <= 2, $"wave of {size}: 1 or 2 corrupted");
                Assert.IsTrue(c[(int)HoldRole.Ranged] >= Math.Min(3, size / 2), $"wave of {size}: ranged, but never most of a small wave");
            }

            Assert.AreEqual(HoldRole.Hunter, WorldEventHoldRules.DropOrder[0], "hunters give way first in a small wave");
        }

        [TestMethod]
        public void Cadence_WhicheverComesLater_WithACeiling()
        {
            // 40 s and 80% dead, both; never later than 75 s.
            Assert.IsFalse(WorldEventHoldRules.HoldNextWaveDue(T0 + 20, T0, 40, 8, 0, 10, 0.2, true, 75), "all dead at 20 s: wait for 40 s");
            Assert.IsTrue(WorldEventHoldRules.HoldNextWaveDue(T0 + 40, T0, 40, 8, 2, 10, 0.2, true, 75), "40 s and 2 of 10 left");
            Assert.IsFalse(WorldEventHoldRules.HoldNextWaveDue(T0 + 50, T0, 40, 8, 3, 10, 0.2, true, 75), "3 of 10 left: not yet");
            Assert.IsTrue(WorldEventHoldRules.HoldNextWaveDue(T0 + 75, T0, 40, 8, 10, 10, 0.2, true, 75), "the ceiling");
            Assert.IsFalse(WorldEventHoldRules.HoldNextWaveDue(T0 + 74, T0, 40, 8, 10, 10, 0.2, true, 75));

            // The either-or rule is unchanged without requireBoth.
            Assert.IsTrue(WorldEventHoldRules.HoldNextWaveDue(T0 + 20, T0, 40, 8, 0, 10, 0.2, false, 75), "either: a clear field is enough");
        }

        [TestMethod]
        public void UndefendedSeconds_ScaleWithTheGroup_WithAFloor()
        {
            var p = new ProtectDef { UndefendedSeconds = 150, UndefendedSecondsPerParticipant = 15, UndefendedSecondsFloor = 100 };

            Assert.AreEqual(150, p.EffectiveUndefendedSeconds(0), 1e-9, "nobody counted reads as one");
            Assert.AreEqual(150, p.EffectiveUndefendedSeconds(1), 1e-9);
            Assert.AreEqual(135, p.EffectiveUndefendedSeconds(2), 1e-9);
            Assert.AreEqual(105, p.EffectiveUndefendedSeconds(4), 1e-9);
            Assert.AreEqual(100, p.EffectiveUndefendedSeconds(5), 1e-9, "the floor");
            Assert.AreEqual(100, p.EffectiveUndefendedSeconds(40), 1e-9);

            Assert.AreEqual(150, new ProtectDef { UndefendedSeconds = 150 }.EffectiveUndefendedSeconds(10), 1e-9, "no per-participant setting: one value");
            Assert.AreEqual(0, new ProtectDef().EffectiveUndefendedSeconds(3), 1e-9, "no time-based damage stays off");
        }

        [TestMethod]
        public void TestLevel_CountsAsOneParticipant_ForWaveSizeAndDamage()
        {
            var goal = BuildHoldGoal();
            goal.Protect.WaveSize = new ScaledCount { Base = 2, PerParticipant = 2.5, Cap = 40 };
            goal.Protect.UndefendedSeconds = 150;
            goal.Protect.UndefendedSecondsPerParticipant = 15;

            var evt = BuildHoldEvent(goal, new HoldHarness(goal).Objective, testLevel: 150, audience: () => new AudienceEstimate(0, 0, 0));
            evt.Stage(T0);
            evt.Tick(T0);

            Assert.AreEqual(1, evt.Audience.Count, "an admin alone with --level is one participant");
            Assert.AreEqual(5, goal.Protect.WaveSize.Resolve(evt.Audience.Count), "so a wave is 5 monsters");
            Assert.AreEqual(150, goal.Protect.EffectiveUndefendedSeconds(evt.ResolvedAudienceCount), 1e-9, "and damage is the solo value");
        }

        // ---- settings -------------------------------------------------------------------------------------------

        [TestMethod]
        public void GoalsJson_SurvivalSettings_Validate()
        {
            void Refused(string with, string expected)
            {
                var json = SampleProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, " + with);
                var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(json, holdSeconds: 480), null, null);

                Assert.IsFalse(store.Goals.ContainsKey("hold_wards"), with);
                Assert.IsTrue(store.Diagnostics.Any(d => d.Contains(expected)), string.Join(" | ", store.Diagnostics));
            }

            Refused(@"""healBudgetPerWave"": 1.5,", "heal share");
            Refused(@"""corruptedKillHeal"": -0.1,", "heal share");
            Refused(@"""clearRegenPerSecond"": 0.5,", "protect.clearRegenPerSecond");
            Refused(@"""clearRadius"": 99,", "protect.clearRadius");
            Refused(@"""shrineCooldownSeconds"": 0,", "protect.shrineCooldownSeconds");
            Refused(@"""shrine"": { ""wcid"": 0 },", "protect.shrine with no wcid");
            Refused(@"""shrine"": { ""name"": ""Shrine of <b>"" },", "protect.shrine.name");
            Refused(@"""shrine"": { ""readyEffect"": ""Sparkles"" },", "protect.shrine effect");
            Refused(@"""undefendedSecondsPerParticipant"": -5,", "protect.undefendedSecondsPerParticipant");
            Refused(@"""maxWaveIntervalSeconds"": -1,", "protect.maxWaveIntervalSeconds");

            var ok = SampleProtectJson.Replace(@"""radius"": 6.0,",
                @"""radius"": 6.0, ""healBudgetPerWave"": 0.25, ""corruptedKillHeal"": 0.1, ""rescueHeal"": 0.01, ""clearRegenPerSecond"": 0.005, ""shrine"": { },");
            var good = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(ok, holdSeconds: 480), null, null);

            Assert.IsTrue(good.Goals.ContainsKey("hold_wards"), string.Join(" | ", good.Diagnostics));
            Assert.AreEqual(2000008u, good.Goals["hold_wards"].Protect.Shrine.Wcid, "an empty shrine block takes the defaults (the DreamWeave shrine)");
        }

        [TestMethod]
        public void SurvivalMechanics_AreOffByDefault_SoOtherGoalsAreUnchanged()
        {
            var p = new ProtectDef();

            Assert.AreEqual(0, p.CorruptedKillHeal);
            Assert.AreEqual(0, p.RescueHeal);
            Assert.AreEqual(0, p.ClearRegenPerSecond);
            Assert.AreEqual(0, p.HealBudgetPerWave, "no budget unless the goal sets one");
            Assert.IsNull(p.Shrine);
            Assert.IsFalse(p.NextWaveRequiresBoth);
            Assert.AreEqual(0, p.UndefendedSecondsPerParticipant);
        }
    }
}
