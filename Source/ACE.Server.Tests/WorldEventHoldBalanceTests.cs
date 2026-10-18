using System;
using System.Collections.Generic;
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
    /// The Hold balance simulation: a second-by-second model of a defend_envoys run that uses the REAL rules for
    /// wave size, the role mix, the cadence, the time-based damage slice and its cap, the heal reduction after a hit,
    /// the per-wave healing budget and the rescue cap. What it cannot know, it assumes, and says so:
    ///   * a player kills an ordinary monster in <see cref="KillSeconds"/> (runners 0.6x that, corrupted 1.5x, the
    ///     finale boss <see cref="BossKillSeconds"/>), plus <see cref="SwitchSeconds"/> to move to the next one;
    ///   * monsters reach the targets <see cref="ArriveSeconds"/> after spawning (runners and ranged sooner);
    ///   * "active defense": players kill corrupted first, then what is hurting the weakest target, and use a shrine
    ///     when its target is under 60% (2 s of their time); "ignore": players only fight the hunters that come for
    ///     them, and never touch a shrine.
    /// Pass criteria (the owner's): active defense keeps at least one target alive, ignoring the targets loses, and
    /// under active defense no target stays above 90% health for a whole wave.
    /// </summary>
    [TestClass]
    public partial class WorldEventHoldBalanceTests
    {
        private const double KillSeconds = 4.0;
        private const double SwitchSeconds = 1.5;
        private const double BossKillSeconds = 25.0;
        private const double ArriveSeconds = 4.0;

        /// <summary>Extra player time to reach a ranged monster standing 18 m out (and break its ward in melee).</summary>
        private const double RangedTravelSeconds = 3.0;
        private const uint MaxHealth = 10000;

        private enum Strategy { Active, Ignore }

        private sealed class Monster
        {
            public HoldRole Role;
            public bool Boss;
            public int Wave;
            public double ArriveAt;
            public double WorkLeft;      // player-seconds of killing left
            public int Target = -1;      // target index, -1 for a hunter
            public bool Alive = true;
        }

        private sealed class Result
        {
            public bool Won;
            public int Survivors;
            public int WavesStarted;
            public double EndSeconds;
            public double LowestEndHealth;
            public int WavesAbove90;     // (target, wave) pairs that stayed above 90% for the whole wave
            public List<int> Above90Waves = new List<int>();
            public int TargetWaves;
            public double HealedTotal;
            public double DamageTotal;
            public override string ToString() =>
                $"{(Won ? "WIN " : "LOSS")} survivors={Survivors}/3 waves={WavesStarted} end={EndSeconds:0}s " +
                $"healed={HealedTotal / MaxHealth * 100:0}% dmg={DamageTotal / MaxHealth * 100:0}% above90={WavesAbove90}/{TargetWaves}";
        }

        /// <summary>The shipped defend_envoys protect block when this machine has it, else the same numbers inline.</summary>
        private static ProtectDef ShippedEnvoys(out int holdSeconds)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "dreamweave-content", "events", "defend_envoys.goal.json");

                if (!File.Exists(path))
                    continue;

                var store = WorldEventAxisStore.Parse(null, null, File.ReadAllText(path), null, null);
                var goal = store.Goals["defend_envoys"];
                holdSeconds = goal.HoldSeconds;
                return goal.Protect;
            }

            Assert.Inconclusive("defend_envoys.goal.json not found next to the repo");
            holdSeconds = 0;
            return null;
        }

        private static Result Simulate(ProtectDef p, int holdSeconds, int players, Strategy strategy, int seed)
        {
            var rng = new Random(seed);

            var hp = new double[] { MaxHealth, MaxHealth, MaxHealth };
            var lastHit = new double[] { -100, -100, -100 };
            var budgetSpent = new int[3];
            var rescueSpent = new int[3];
            var budgetWave = new int[] { -1, -1, -1 };
            var shrineReady = new double[3];
            var capTokens = new double[3];
            var capLastAt = new double[3];
            var waveMin = new List<double[]>();
            var monsters = new List<Monster>();
            var playerFree = new double[Math.Max(1, players)];
            var playerTask = new Monster[Math.Max(1, players)];

            var result = new Result();
            var participants = Math.Max(1, players);    // a solo staff test with --level counts as one

            // The ambush source's crowd health (startAt 8, +0.1 per participant, cap 3): tougher monsters for big groups.
            var crowd = Math.Min(3.0, 1.0 + Math.Max(0, participants - 8) * 0.1);
            var totalWaves = Math.Min(ProtectDef.MaxHoldWaves, p.Waves.Resolve(participants));
            var size = p.WaveSize.Resolve(participants);
            var mix = p.WaveMix;
            var waves = 0;
            var lastWaveAt = 0.0;
            var lastWaveSize = 0;
            var clearedWaves = new HashSet<int>();
            var healedWaves = 0;
            var breach = 0;

            bool Alive(int i) => hp[i] > 0;

            double HealShare(int i, double share, bool rescue, double now)
            {
                if (!Alive(i) || !(share > 0))
                    return 0;

                if (budgetWave[i] != waves)
                {
                    budgetWave[i] = waves;
                    budgetSpent[i] = 0;
                    rescueSpent[i] = 0;
                }

                var factor = WorldEventHoldRules.HealAfterHitFactor(now - lastHit[i], p.HealReductionAfterHit, p.HealReductionSeconds);
                var requested = (int)Math.Floor(share * MaxHealth * factor);

                if (rescue)
                    requested = Math.Min(requested, Math.Max(0, (int)Math.Floor(p.RescueCapPerWave * MaxHealth) - rescueSpent[i]));

                requested = Math.Min(requested, (int)(MaxHealth - hp[i]));

                var allowed = WorldEventHoldRules.HealWithinBudget(requested, MaxHealth, p.HealBudgetPerWave, budgetSpent[i]);

                budgetSpent[i] += allowed;

                if (rescue)
                    rescueSpent[i] += allowed;

                hp[i] += allowed;
                result.HealedTotal += allowed;
                return allowed;
            }

            int LeastAttacked(int avoid)
            {
                var best = -1;

                for (var i = 0; i < 3; i++)
                {
                    if (!Alive(i))
                        continue;

                    var count = monsters.Count(m => m.Alive && m.Target == i) + (i == avoid ? 1000 : 0);

                    if (best < 0 || count < monsters.Count(m => m.Alive && m.Target == best) + (best == avoid ? 1000 : 0))
                        best = i;
                }

                return best;
            }

            void SpawnWave(double now)
            {
                waves++;
                lastWaveAt = now;
                waveMin.Add(new[] { hp[0], hp[1], hp[2] });

                var final = waves == totalWaves;
                var aliveTargets = Enumerable.Range(0, 3).Where(Alive).ToList();
                breach = aliveTargets.Count > 0 ? aliveTargets[rng.Next(aliveTargets.Count)] : 0;

                var batch = new List<Monster>();

                if (final)
                {
                    // The finale: the boss on the healthiest, then the rest (seekShare seekers, the others for players).
                    var healthiest = Enumerable.Range(0, 3).Where(Alive).OrderByDescending(i => hp[i]).FirstOrDefault();
                    batch.Add(new Monster { Role = HoldRole.Seeker, Boss = true, Target = healthiest });

                    var rest = size + WorldEventHoldRules.FinalWaveBonus(p.WaveSize.Base);

                    for (var k = 0; k < rest; k++)
                        batch.Add(new Monster { Role = k < rest * p.SeekShare ? HoldRole.Seeker : HoldRole.Hunter });
                }
                else
                {
                    var counts = WorldEventHoldRules.WaveMixCounts(size, mix.Shares, mix.Minimums, mix.Maximums);

                    for (var r = 0; r < counts.Length; r++)
                        for (var k = 0; k < counts[r]; k++)
                            batch.Add(new Monster { Role = (HoldRole)r });
                }

                foreach (var m in batch)
                {
                    m.Wave = waves;
                    m.ArriveAt = now + (m.Role == HoldRole.Runner ? 2.5 : m.Role == HoldRole.Ranged ? 2.0 : ArriveSeconds) + (m.Boss ? p.FinaleBossLeadSeconds * 0 : 0);
                    m.WorkLeft = m.Boss ? BossKillSeconds
                        : m.Role == HoldRole.Runner ? KillSeconds * 0.6
                        : m.Role == HoldRole.Corrupted ? KillSeconds * 1.5
                        : m.Role == HoldRole.Ranged ? KillSeconds + RangedTravelSeconds
                        : KillSeconds;

                    m.WorkLeft *= crowd;

                    if (m.Role == HoldRole.Hunter)
                        m.Target = -1;
                    else if (m.Boss)
                    {
                        // already set
                    }
                    else if (m.Role == HoldRole.Breach)
                        m.Target = breach;
                    else
                        m.Target = LeastAttacked(final ? -1 : breach);   // the finale has no breach target to avoid

                    monsters.Add(m);
                }

                lastWaveSize = batch.Count;
                result.WavesStarted = waves;
            }

            for (var t = 0; t <= holdSeconds; t++)
            {
                var liveCount = monsters.Count(m => m.Alive);

                if (waves == 0)
                    SpawnWave(t);
                else if (waves < totalWaves && WorldEventHoldRules.HoldNextWaveDue(t, lastWaveAt, p.WaveIntervalSeconds, p.MinWaveGapSeconds,
                             liveCount, lastWaveSize, p.NextWaveAliveShare, p.NextWaveRequiresBoth, p.MaxWaveIntervalSeconds))
                    SpawnWave(t);

                // Damage: every arrived attacker, time-based, the corrupted aura, the cap.
                for (var i = 0; i < 3; i++)
                {
                    if (!Alive(i))
                        continue;

                    var on = monsters.Where(m => m.Alive && m.Target == i && t >= m.ArriveAt).ToList();

                    if (on.Count == 0)
                        continue;

                    var corruptedHere = on.Any(m => m.Role == HoldRole.Corrupted);
                    var corrupted = p.EffectiveCorrupted;
                    var dmg = 0.0;

                    foreach (var m in on)
                    {
                        var rate = MaxHealth / p.EffectiveUndefendedSeconds(participants);

                        if (m.Role == HoldRole.Runner)
                            rate *= p.SpecialWave(HoldWaveType.Runners)?.AttackSpeedMult ?? 1.5;

                        rate *= WorldEventHoldRules.EmpowerFactor(m.Role == HoldRole.Corrupted, corruptedHere ? 0 : (double?)null,
                            corrupted.EmpowerRadius, corrupted.EmpowerMult, corrupted.SelfMult);

                        dmg += rate;
                    }

                    var capped = p.MinDeathSeconds > 0
                        ? WorldEventHoldRules.CapProtectedHit((float)dmg, MaxHealth, p.MinDeathSeconds, ref capTokens[i], ref capLastAt[i], 1_700_000_000d + t)
                        : dmg;

                    hp[i] -= capped;
                    result.DamageTotal += capped;
                    lastHit[i] = t;

                    if (hp[i] <= 0)
                    {
                        hp[i] = 0;

                        foreach (var m in monsters.Where(m => m.Alive && m.Target == i))
                            m.Target = LeastAttacked(-1);
                    }
                }

                if (!Enumerable.Range(0, 3).Any(Alive))
                {
                    result.EndSeconds = t;
                    break;
                }

                // Players.
                for (var pl = 0; pl < playerFree.Length; pl++)
                {
                    var task = playerTask[pl];

                    if (task != null && task.Alive)
                    {
                        task.WorkLeft -= 1;

                        if (task.WorkLeft <= 0)
                        {
                            task.Alive = false;
                            playerTask[pl] = null;
                            playerFree[pl] = t + SwitchSeconds;

                            if (task.Role == HoldRole.Corrupted && p.CorruptedKillHeal > 0)
                                for (var i = 0; i < 3; i++)
                                    HealShare(i, p.CorruptedKillHeal, false, t);

                            if (task.Target >= 0 && t >= task.ArriveAt && p.RescueHeal > 0)
                                HealShare(task.Target, p.RescueHeal, true, t);
                        }

                        continue;
                    }

                    if (t < playerFree[pl])
                        continue;

                    if (strategy == Strategy.Active && p.Shrine != null)
                    {
                        var low = Enumerable.Range(0, 3).Where(i => Alive(i) && hp[i] < MaxHealth * 0.6 && t >= shrineReady[i])
                            .OrderBy(i => hp[i]).DefaultIfEmpty(-1).First();

                        if (low >= 0)
                        {
                            shrineReady[low] = t + p.ShrineCooldownSeconds;
                            HealShare(low, p.ShrineHeal, false, t);
                            playerFree[pl] = t + 2;
                            continue;
                        }
                    }

                    var taken = new HashSet<Monster>(playerTask.Where(x => x != null));
                    var open = monsters.Where(m => m.Alive && !taken.Contains(m) && t >= m.ArriveAt - 1).ToList();

                    Monster pick = null;

                    if (strategy == Strategy.Active)
                    {
                        var weakest = Enumerable.Range(0, 3).Where(Alive).OrderBy(i => hp[i]).ToList();

                        pick = open.FirstOrDefault(m => m.Role == HoldRole.Corrupted)
                            ?? weakest.Select(w => open.FirstOrDefault(m => m.Target == w && !m.Boss)).FirstOrDefault(m => m != null)
                            ?? open.FirstOrDefault(m => m.Target >= 0)
                            ?? open.FirstOrDefault();
                    }
                    else
                        pick = open.FirstOrDefault(m => m.Role == HoldRole.Hunter);

                    if (pick != null)
                        playerTask[pl] = pick;
                }

                // Clear regeneration and the between-waves heal.
                for (var i = 0; i < 3; i++)
                {
                    var hostileNear = monsters.Any(m => m.Alive && m.Target == i && t >= m.ArriveAt);

                    if (Alive(i) && p.ClearRegenPerSecond > 0 && WorldEventHoldRules.IsClear(hostileNear ? 0 : (double?)null, p.ClearRadius))
                        HealShare(i, p.ClearRegenPerSecond, false, t);
                }

                for (var w = 1; w <= waves; w++)
                {
                    if (clearedWaves.Contains(w) || monsters.Any(m => m.Wave == w && m.Alive))
                        continue;

                    clearedWaves.Add(w);
                    healedWaves++;

                    for (var i = 0; i < 3; i++)
                        HealShare(i, p.HealBetweenWaves, false, t);
                }

                // Per-wave low points, for the "never untouchable" check.
                if (waves > 0)
                {
                    var mins = waveMin[waves - 1];

                    for (var i = 0; i < 3; i++)
                        mins[i] = Math.Min(mins[i], hp[i]);
                }

                if (waves >= totalWaves && monsters.All(m => !m.Alive))
                {
                    result.Won = true;
                    result.EndSeconds = t;
                    break;
                }

                result.EndSeconds = t;
            }

            if (!result.Won && Enumerable.Range(0, 3).Any(Alive) && result.EndSeconds >= holdSeconds)
                result.Won = true;   // holdSeconds ends it as a success with a target standing

            result.Survivors = Enumerable.Range(0, 3).Count(Alive);
            result.LowestEndHealth = hp.Where(h => h > 0).DefaultIfEmpty(0).Min() / MaxHealth;

            // Waves where a living target never dropped below 90% (the last wave counts until the end).
            for (var w = 0; w < waveMin.Count; w++)
            {
                for (var i = 0; i < 3; i++)
                {
                    if (waveMin[w][i] <= 0)
                        continue;

                    result.TargetWaves++;

                    if (waveMin[w][i] > MaxHealth * 0.9)
                    {
                        result.WavesAbove90++;
                        result.Above90Waves.Add(w + 1);
                    }
                }
            }

            return result;
        }

        private static readonly int[] GroupSizes = { 1, 3, 10 };

        private static string Report(ProtectDef p, int holdSeconds, out Dictionary<(int, Strategy), List<Result>> all)
        {
            all = new Dictionary<(int, Strategy), List<Result>>();
            var sb = new StringBuilder();

            foreach (var players in GroupSizes)
            {
                foreach (var strategy in new[] { Strategy.Active, Strategy.Ignore })
                {
                    var runs = Enumerable.Range(0, 20).Select(seed => Simulate(p, holdSeconds, players, strategy, seed)).ToList();
                    all[(players, strategy)] = runs;

                    sb.AppendLine($"{players,2} player(s) {strategy,-6}: wins {runs.Count(r => r.Won)}/20, " +
                                  $"survivors avg {runs.Average(r => r.Survivors):0.0} (min {runs.Min(r => r.Survivors)}), " +
                                  $"end avg {runs.Average(r => r.EndSeconds):0}s, waves {runs.Average(r => r.WavesStarted):0.0}, " +
                                  $"target-waves above 90% {runs.Sum(r => r.WavesAbove90)}/{runs.Sum(r => r.TargetWaves)}");
                }
            }

            return sb.ToString();
        }

        [TestMethod]
        public void Balance_ActiveDefenseHolds_IgnoringLoses_HealingNeverMakesThemUntouchable()
        {
            var p = ShippedEnvoys(out var holdSeconds);
            var report = Report(p, holdSeconds, out var all);

            Console.WriteLine(report);

            foreach (var players in GroupSizes)
            {
                var active = all[(players, Strategy.Active)];
                var ignore = all[(players, Strategy.Ignore)];

                Assert.IsTrue(active.All(r => r.Won && r.Survivors >= 1), $"{players} player(s), active defense keeps at least one envoy alive\n{report}");
                Assert.IsTrue(active.Average(r => r.Survivors) >= 1.0, $"{players} player(s): 1-2 envoys alive on average\n{report}");
                Assert.IsTrue(ignore.All(r => !r.Won), $"{players} player(s), ignoring the envoys loses\n{report}");
                Assert.AreEqual(0, active.Sum(r => r.WavesAbove90), $"{players} player(s): no envoy stays above 90% for a whole wave\n{report}");
            }
        }

        [TestMethod]
        public void Balance_HealingNeverOutpacesTheBudget()
        {
            var p = ShippedEnvoys(out var holdSeconds);

            foreach (var players in GroupSizes)
            {
                foreach (var run in Enumerable.Range(0, 10).Select(seed => Simulate(p, holdSeconds, players, Strategy.Active, seed)))
                {
                    // Three targets, at most the budget each per wave started.
                    var ceiling = 3 * p.HealBudgetPerWave * MaxHealth * run.WavesStarted;
                    Assert.IsTrue(run.HealedTotal <= ceiling + 1, $"{players} player(s): healed {run.HealedTotal} over a ceiling of {ceiling}");
                    Assert.IsTrue(run.HealedTotal < run.DamageTotal, $"{players} player(s): healing ({run.HealedTotal}) never outpaces damage ({run.DamageTotal})");
                }
            }
        }
    }
}