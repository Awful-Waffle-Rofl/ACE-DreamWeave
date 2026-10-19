using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// "A run with all defaults matches today's behavior" (the live-tuning change, 2026-10-19). With no Hold setting set,
    /// every value the Hold code resolves for the shipped defend_envoys, every exposed constant, the wave and heal rules
    /// over a range of inputs, and a fixed-seed run of the balance model must equal the baseline captured BEFORE the
    /// live-tuning change (TestData/hold-defaults-baseline.txt). Any line that moves names what changed.
    ///
    /// To re-capture after a deliberate balance change: delete the file and run this test once (it writes the file and
    /// reports Inconclusive), then commit the new file with the change that explains it.
    /// </summary>
    public partial class WorldEventHoldBalanceTests
    {
        private const string BaselineFile = "hold-defaults-baseline.txt";

        [TestMethod]
        public void Defaults_MatchTheBehaviorBeforeLiveTuning()
        {
            var actual = Fingerprint();
            var path = BaselinePath();

            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, actual);
                Assert.Inconclusive($"no baseline yet; wrote {path}. Re-run to compare.");
            }

            var expected = File.ReadAllText(path).Replace("\r\n", "\n");
            var expectedLines = expected.Split('\n');
            var actualLines = actual.Replace("\r\n", "\n").Split('\n');

            for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length); i++)
            {
                var e = i < expectedLines.Length ? expectedLines[i] : "(missing)";
                var a = i < actualLines.Length ? actualLines[i] : "(missing)";

                Assert.AreEqual(e, a, $"line {i + 1} of {BaselineFile} moved with no Hold setting set");
            }
        }

        private static string BaselinePath()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var project = Path.Combine(dir.FullName, "ACE.Server.Tests.csproj");

                if (File.Exists(project))
                    return Path.Combine(dir.FullName, "TestData", BaselineFile);

                var nested = Path.Combine(dir.FullName, "Source", "ACE.Server.Tests", "ACE.Server.Tests.csproj");

                if (File.Exists(nested))
                    return Path.Combine(dir.FullName, "Source", "ACE.Server.Tests", "TestData", BaselineFile);
            }

            Assert.Inconclusive("ACE.Server.Tests project folder not found");
            return null;
        }

        private static GoalDef ShippedEnvoysGoal()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "dreamweave-content", "events", "defend_envoys.goal.json");

                if (!File.Exists(path))
                    continue;

                return WorldEventAxisStore.Parse(null, null, File.ReadAllText(path), null, null).Goals["defend_envoys"];
            }

            Assert.Inconclusive("defend_envoys.goal.json not found next to the repo");
            return null;
        }

        private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        internal static string Fingerprint()
        {
            var goal = ShippedEnvoysGoal();
            var p = goal.Protect;
            var sb = new StringBuilder();

            void L(string name, object value) => sb.Append(name).Append(" = ").Append(value is double d ? N(d) : value is float f ? N(f) : Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');

            // ---- the goal and the protect block, through the accessors the game code reads ----
            L("goal.holdSeconds", goal.HoldSeconds);
            L("knights.count(1..12)", string.Join(",", Enumerable.Range(1, 12).Select(n => p.ResolveCount(n))));
            L("knights.layout(1..12)", string.Join(",", Enumerable.Range(1, 12).Select(n => { var l = p.ResolveLayout(n); return $"{l.Count}@{N(l.Radius)}"; })));
            L("knights.maxHealth(1..12)", string.Join(",", Enumerable.Range(1, 12).Select(n => p.EffectiveMaxHealth(n))));
            L("knights.undefended(1..12)", string.Join(",", Enumerable.Range(1, 12).Select(n => N(p.EffectiveUndefendedSeconds(n)))));
            L("knights.minDeathSeconds", p.MinDeathSeconds);
            L("knights.minSurvivors", p.MinSurvivors);
            L("knights.healthMilestones", p.HealthMilestones);
            L("knights.showHealthBar", p.ShowHealthBar);
            L("knights.defendPose", p.DefendPose == null ? "none" : $"{N(p.DefendPose.Reach)}/{N(p.DefendPose.SwingSeconds)}/{N(p.DefendPose.SwingMaxSeconds)}/{N(p.DefendPose.TurnBackSeconds)}/{N(p.DefendPose.FaceDegrees)}");
            L("knights.timeScaledMaxHealth", ProtectDef.TimeScaledMaxHealth);
            L("knights.maxTargets", ProtectDef.MaxProtectedTargets);
            L("knights.placementGrace", HoldObjective.PlacementGraceSeconds);

            L("heal.shrine", p.Shrine == null ? "none" : $"{p.Shrine.Wcid}/{N(p.Shrine.UseRadius)}/{N(p.Shrine.ReadyPulseSeconds)}");
            L("heal.shrineHeal", p.ShrineHeal);
            L("heal.shrineCooldown", p.ShrineCooldownSeconds);
            L("heal.corruptedKillHeal", p.CorruptedKillHeal);
            L("heal.rescue", $"{N(p.RescueHeal)}/{N(p.RescueCapPerWave)}");
            L("heal.regen", $"{N(p.ClearRegenPerSecond)}/{N(p.ClearRadius)}");
            L("heal.playerCap", p.MaxHealPerTenSeconds);
            L("heal.reduction", $"{N(p.HealReductionAfterHit)}/{N(p.HealReductionSeconds)}");
            L("heal.betweenWaves", p.HealBetweenWaves);
            L("heal.budget", p.HealBudgetPerWave);

            var mix = p.WaveMix;
            L("mix.shares", string.Join(",", mix.Shares.Select(N)));
            L("mix.minimums", string.Join(",", mix.Minimums));
            L("mix.maximums", string.Join(",", mix.Maximums));
            var runners = p.SpecialWave(HoldWaveType.Runners);
            var ranged = p.SpecialWave(HoldWaveType.Ranged);
            L("runners", runners == null ? "none" : $"{N(runners.CountMult)}/{N(runners.HealthMult)}/{N(runners.SpeedMult)}/{N(runners.AttackSpeedMult)}");
            L("ranged.holdDistance", ranged?.HoldDistance ?? -1);
            var c = p.EffectiveCorrupted;
            L("corrupted", $"{N(c.Scale)}/{N(c.HealthMult)}/{N(c.EmpowerRadius)}/{N(c.EmpowerMult)}/{N(c.SelfMult)}/{c.AssaultEveryWaves}/{c.AssaultFromWave}/{c.AssaultCount}/{c.TintPalette}");
            L("seekShare", p.EffectiveSeekShare);

            L("waves.count(1..12)", string.Join(",", Enumerable.Range(1, 12).Select(n => p.Waves.Resolve(n))));
            L("waves.size(0..40)", string.Join(",", Enumerable.Range(0, 41).Select(n => p.WaveSize.Resolve(n))));
            L("waves.maxAlive", p.MaxAlive);
            L("waves.interval", p.WaveIntervalSeconds);
            L("waves.cadence", $"{N(p.NextWaveAliveShare)}/{p.NextWaveRequiresBoth}/{N(p.MaxWaveIntervalSeconds)}/{N(p.MinWaveGapSeconds)}/{N(p.MinSeconds)}");
            L("waves.spawnRadius", p.SpawnRadius);
            L("waves.portals", p.Portals == null ? "none" : $"{p.Portals.Count}/{N(p.Portals.Radius)}/{N(p.Portals.Scale)}");
            L("waves.maxHoldWaves", ProtectDef.MaxHoldWaves);
            L("waves.minSpawnClearance", ProtectDef.MinSpawnClearance);
            L("waves.finalBonus(0..40)", string.Join(",", Enumerable.Range(0, 41).Select(WorldEventHoldRules.FinalWaveBonus)));
            L("waves.mixCounts(1..45)", string.Join(";", Enumerable.Range(1, 45).Select(n => string.Join(",", WorldEventHoldRules.WaveMixCounts(n, mix.Shares, mix.Minimums, mix.Maximums)))));
            L("waves.assaults(1..10)", string.Join(",", Enumerable.Range(1, 10).Select(w => WorldEventHoldRules.IsAssaultWave(w, c.AssaultEveryWaves, c.AssaultFromWave, w == 10))));

            L("finale", $"{N(p.FinaleBossScale)}/{N(p.FinaleBossHealthMult)}/{N(p.FinaleBossLeadSeconds)}/{p.FinaleBossPrefix}/{p.BossTargetsProtected}");
            L("finale.boss", $"{N(p.BossMinKillSeconds)}/{N(p.BossSwapSeconds)}/{N(p.BossSwapMaxSeconds)}/{N(p.BossPullShare)}/{N(p.BossPullWindowSeconds)}/{N(p.BossPullSeconds)}");
            L("finale.scale(cfg 0, x0.67)", WorldEventHoldRules.FinaleBossScale(p.FinaleBossScale, 0.67f));

            // ---- the pure rules over a range of inputs ----
            L("rules.hitInterval", $"{N(WorldEventHoldRules.HitInterval(null, 100))}/{N(WorldEventHoldRules.HitInterval(99, 100))}/{N(WorldEventHoldRules.HitInterval(90, 100))}");
            L("rules.hitDamage", string.Join(",", new[] { 0.5, 1, 2, 3, 4, 8 }.Select(i => N(WorldEventHoldRules.ProtectedHitDamage(10000, i, 200)))));
            L("rules.betweenWaves", WorldEventHoldRules.BetweenWavesHeal(10000, p.HealBetweenWaves));
            L("rules.budget", string.Join(",", new[] { 0, 1000, 3000, 3500 }.Select(spent => WorldEventHoldRules.HealWithinBudget(1500, 10000, p.HealBudgetPerWave, spent))));
            {
                double tokens = 0, lastAt = 0;
                L("rules.playerHeal", string.Join(",", new[] { 50.0, 100, 400, 1000, 1000 }.Select((h, i) => WorldEventHoldRules.ConvertPlayerHeal(h, 10000, p.MaxHealPerTenSeconds, ref tokens, ref lastAt, 1000 + i))));
            }
            {
                double tokens = 0, lastAt = 0;
                L("rules.cap", string.Join(",", Enumerable.Range(0, 8).Select(i => N(WorldEventHoldRules.CapProtectedHit(2000, 10000, p.MinDeathSeconds, ref tokens, ref lastAt, 1000 + i)))));
            }
            L("rules.healAfterHit", string.Join(",", new double?[] { null, 0, 2, 5, 6 }.Select(s => N(WorldEventHoldRules.HealAfterHitFactor(s, p.HealReductionAfterHit, p.HealReductionSeconds)))));
            L("rules.empower", $"{N(WorldEventHoldRules.EmpowerFactor(true, null, c.EmpowerRadius, c.EmpowerMult, c.SelfMult))}/{N(WorldEventHoldRules.EmpowerFactor(false, 5, c.EmpowerRadius, c.EmpowerMult, c.SelfMult))}");
            L("rules.rangedSlack", WorldEventHoldRules.RangedHoldSlack);
            L("rules.runnersKeep(1..12)", string.Join(",", Enumerable.Range(1, 12).Select(n => WorldEventHoldRules.RunnersKeep(n, runners?.CountMult ?? 1))));

            // ---- the chain ----
            var ch = goal.Chain;
            L("chain", ch == null ? "none" : $"{ch.MaxLength}/{ch.CountdownSeconds}/{N(ch.MinDistance)}/{N(ch.MaxDistance)}/{ch.ToAnotherTown}/{N(ch.TownMinDistance)}/{N(ch.TownMaxDistance)}/{ch.TownChoices}/{N(ch.TownSpotMinDistance)}/{N(ch.TownSpotMaxDistance)}");
            L("chain.next", ch == null ? "none" : $"{ch.NextSource}/{ch.NextGoal}/{ch.NextBoss}/{string.Join("+", ch.StageRewards ?? new List<string>())}");
            L("chain.pacing", ch?.Pacing == null ? "none" : $"{N(ch.Pacing.ThanksSeconds)}/{N(ch.Pacing.StorySeconds)}/{N(ch.Pacing.AnnounceSeconds)}/{N(ch.Pacing.TieBackSeconds)}/{N(ch.Pacing.ClosingSeconds)}/{N(ch.Pacing.AsheronSeconds)}");
            var tr = goal.TownReach;
            L("townReach", tr == null ? "none" : $"{N(tr.MaxWalk)}/{N(tr.SpotMinDistance)}/{N(tr.SpotMaxDistance)}/{tr.Towns}");

            // ---- the balance model, fixed seeds ----
            foreach (var players in GroupSizes)
            {
                foreach (var strategy in new[] { Strategy.Active, Strategy.NoFocus, Strategy.Ignore })
                {
                    for (var seed = 0; seed < 6; seed++)
                    {
                        var r = Simulate(p, goal.HoldSeconds, players, strategy, seed);
                        L($"sim.{players}.{strategy}.{seed}", $"{r} boss={N(Math.Round(r.BossDamage, 3))}/{r.BossSwaps}/{r.BossOnTargetSeconds}/{r.BossPulledSeconds}/{r.BossKills} low={N(Math.Round(r.LowestEndHealth, 6))}");
                    }
                }
            }

            return sb.ToString();
        }
    }
}
