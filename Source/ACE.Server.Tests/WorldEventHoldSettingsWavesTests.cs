using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The waves and internals groups of the Hold settings.</summary>
    public partial class WorldEventHoldSettingsTests
    {
        [TestMethod]
        public void Waves_WithNothingSet_ReadExactlyTheGoalsJson()
        {
            var goal = EnvoysGoal();
            var p = goal.Protect;

            using var scope = new HoldSettingsScope();

            Assert.AreSame(p.WaveSizeJson, p.WaveSize, "not set: goals.json's own object");
            Assert.AreSame(p.WavesJson, p.Waves);
            Assert.AreEqual(ProtectDef.MaxHoldWaves, ProtectDef.EffectiveMaxHoldWaves);
            Assert.AreEqual(5, p.FirstWaveDelaySeconds);
            Assert.AreEqual(ProtectDef.MinSpawnClearance, ProtectDef.EffectiveMinSpawnClearance);
            Assert.AreEqual(480, goal.HoldSeconds);
            Assert.AreEqual(2, WorldEventHoldRules.FinalWaveBonus(5));

            foreach (var group in new[] { HoldSettings.GroupWaves, HoldSettings.GroupInternals })
                foreach (var s in HoldSettings.InGroup(group).Where(s => !s.IsReused))
                    Assert.IsFalse(HoldSettings.Describe(s, goal).Overridden, s.Key);
        }

        [TestMethod]
        public void Waves_SizeAndCountAreLive_AndOwnTheirDials()
        {
            var goal = EnvoysGoal();
            var p = goal.Protect;

            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.WaveSizeBase, "5");
            Assert.AreEqual(5, p.WaveSize.Base);
            Assert.AreEqual(2.5, p.WaveSize.PerParticipant, "the rest stays goals.json's");
            Assert.IsTrue(p.WaveSize.OwnsBase && !p.WaveSize.OwnsPerParticipant);
            Assert.AreEqual(8, p.WaveSize.Resolve(1), "5 + ceil(2.5)");

            // On the theme copy a Hold-owned dial is not replaced by the global world_events_wave_count_base.
            var theme = new SourceThemeDef { WaveCount = new ScaledCount { Base = 3, PerParticipant = 1, Cap = 16 } }.WithHoldSizing(p.WaveSize, 0);
            Assert.AreEqual(5, theme.EffectiveWaveCountBase);

            scope.Set(HoldKeys.WaveSizeBasePlayers, "2");
            Assert.AreEqual(5, p.WaveSize.Resolve(2), "two players are covered by the base");
            Assert.AreEqual(8, p.WaveSize.Resolve(3));

            scope.Set(HoldKeys.WaveSizeMaxPlayers, "4");
            Assert.AreEqual(p.WaveSize.Resolve(4), p.WaveSize.Resolve(30), "growth stops at 4 players");

            scope.Set(HoldKeys.WaveSizeCap, "3");
            Assert.AreEqual(5, p.WaveSize.Cap, "a cap under the base is raised to it");

            scope.Set(HoldKeys.WaveCountBase, "8");
            scope.Set(HoldKeys.WaveCountCap, "8");
            Assert.AreEqual(8, p.Waves.Resolve(1));
            Assert.AreEqual(8, HoldObjective.CapWaves(p.Waves.Resolve(1)));

            scope.Set(HoldKeys.MaxWaves, "6");
            Assert.AreEqual(6, HoldObjective.CapWaves(p.Waves.Resolve(1)), "max_waves is the ceiling");
        }

        [TestMethod]
        public void Waves_CadenceTimersAndFinalBonus()
        {
            var goal = EnvoysGoal();
            var p = goal.Protect;

            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.WaveIntervalSeconds, "20");
            scope.Set(HoldKeys.BreatherSeconds, "0");
            scope.Set(HoldKeys.MaxWaveIntervalSeconds, "30");
            scope.Set(HoldKeys.NextWaveAliveShare, "0.5");
            scope.Set(HoldKeys.FirstWaveDelaySeconds, "0");
            scope.Set(HoldKeys.SpawnRadius, "35");
            scope.Set(HoldKeys.PortalCount, "2");
            scope.Set(HoldKeys.PortalRadius, "40");
            scope.Set(HoldKeys.HoldSeconds, "300");

            Assert.AreEqual(20, p.WaveIntervalSeconds);
            Assert.AreEqual(0, p.MinWaveGapSeconds, "a 0 breather on purpose");
            Assert.AreEqual(30, p.MaxWaveIntervalSeconds);
            Assert.AreEqual(0.5, p.NextWaveAliveShare);
            Assert.AreEqual(0, p.FirstWaveDelaySeconds);
            Assert.AreEqual(35, p.SpawnRadius);
            Assert.AreEqual(2, p.Portals.Count);
            Assert.AreEqual(40, p.Portals.Radius);
            Assert.AreEqual(300, goal.HoldSeconds);

            var killCount = new GoalDef { HoldSeconds = 0, TypeKind = GoalType.KillCount };
            Assert.AreEqual(0, killCount.HoldSeconds, "hold_seconds only touches Hold goals");

            scope.Set(HoldKeys.FinalWaveBonus, "0");
            Assert.AreEqual(0, WorldEventHoldRules.FinalWaveBonus(8), "0 = no bonus");

            scope.Set(HoldKeys.FinalWaveBonus, "1");
            Assert.AreEqual(8, WorldEventHoldRules.FinalWaveBonus(8));
        }

        [TestMethod]
        public void Waves_RunTimers_TheTimeLimitAndAbandon()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();

            var request = new WorldEventRequest();
            HoldSettings.ApplyRunTimers(request, goal);
            Assert.AreEqual(WorldEvent.DefaultMaxDurationSeconds, request.MaxDurationSeconds, "not set: unchanged");
            Assert.AreEqual(WorldEvent.DefaultAbandonAfterSeconds, request.AbandonAfterSeconds);

            scope.Set(HoldKeys.MaxSeconds, "900");
            scope.Set(HoldKeys.AbandonSeconds, "0");
            HoldSettings.ApplyRunTimers(request, goal);
            Assert.AreEqual(900, request.MaxDurationSeconds);
            Assert.AreEqual(0, request.AbandonAfterSeconds, "0 = never abandoned");

            var explicitAbandon = new WorldEventRequest { AbandonAfterSeconds = 45, AbandonExplicit = true };
            HoldSettings.ApplyRunTimers(explicitAbandon, goal);
            Assert.AreEqual(45, explicitAbandon.AbandonAfterSeconds, "--abandon wins for one run");

            var other = new WorldEventRequest();
            HoldSettings.ApplyRunTimers(other, new GoalDef { TypeKind = GoalType.KillBoss });
            Assert.AreEqual(WorldEvent.DefaultMaxDurationSeconds, other.MaxDurationSeconds, "other goals are untouched");

            scope.Set(HoldKeys.MaxSeconds, "100");
            var low = new WorldEventRequest();
            HoldSettings.ApplyRunTimers(low, goal);
            Assert.IsTrue(low.MaxDurationSeconds > low.MinDurationSeconds, "clamped above the minimum duration");
        }

        [TestMethod]
        public void Internals_DefaultToTheOldConstants_AndAreLive()
        {
            using var scope = new HoldSettingsScope();

            Assert.AreEqual(2.0, WorldEventHoldRules.HitInterval(null, 100));
            Assert.AreEqual(4.0, WorldEventHoldRules.HitInterval(50, 100));
            Assert.IsFalse(WorldEventHoldRules.RangedHoldShouldApproach(19, 18));

            scope.Set(HoldKeys.HitIntervalSeconds, "1");
            scope.Set(HoldKeys.HitIntervalMaxSeconds, "8");
            scope.Set(HoldKeys.RangedSlack, "0");
            scope.Set(HoldKeys.SpawnClearance, "20");

            Assert.AreEqual(1.0, WorldEventHoldRules.HitInterval(null, 100));
            Assert.AreEqual(8.0, WorldEventHoldRules.HitInterval(50, 100));
            Assert.IsTrue(WorldEventHoldRules.RangedHoldShouldApproach(19, 18));
            Assert.AreEqual(20, ProtectDef.EffectiveMinSpawnClearance);
        }
    }
}
