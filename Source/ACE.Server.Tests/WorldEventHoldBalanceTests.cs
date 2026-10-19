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
    ///   * a player kills an ordinary monster in <see cref="KillSeconds"/> (runners 0.6x that, corrupted their health
    ///     multiple plus <see cref="CorruptedTravelSeconds"/>, the finale boss <see cref="BossKillSeconds"/>), plus
    ///     <see cref="SwitchSeconds"/> to move to the next one. Calibrated on the 2026-10-18 solo run's log: about one
    ///     ordinary kill every 5.5 s while busy, and the 39,150-health Warlord down in 48 s;
    ///   * monsters reach the targets <see cref="ArriveSeconds"/> after spawning (runners and ranged sooner), and
    ///     ranged monsters attack (the fixed hold), so every wave monster deals damage;
    ///   * "active defense": players drop what they are doing for an arrived corrupted assault monster (up to
    ///     <see cref="CorruptedHands"/> on one), then kill what is hurting the weakest target, and use a shrine when its
    ///     target is under 60% (2 s of their time); "no focus": the same, but a corrupted monster is just another
    ///     monster; "ignore": players only fight the hunters that come for them, and never touch a shrine.
    /// Pass criteria (the owner's): active defense keeps at least one target alive, ignoring the targets loses,
    /// ignoring the corrupted assault costs targets, and healing never makes them untouchable.
    /// </summary>
    [TestClass]
    public partial class WorldEventHoldBalanceTests
    {
        private const double KillSeconds = 4.0;
        private const double SwitchSeconds = 1.5;
        private const double BossKillSeconds = 45.0;
        private const double ArriveSeconds = 5.0;

        /// <summary>
        /// Extra player time per corrupted assault monster, on top of its kill. Fitted to the second live run (2026-10-18
        /// 20:16, solo, no shrine used, corrupted x3): it ended with 1 envoy; the model gives 1.9 on average at 2 s and
        /// 0.5 at 3 s (it ticks whole seconds). The log measured about 17 s per corrupted from arrival; the model needs
        /// less because it is pessimistic elsewhere (every monster attacks the moment it arrives).
        /// </summary>
        internal static double CorruptedTravelSeconds = 2.0;

        /// <summary>Extra player time to reach a ranged monster standing 18 m out (and break its ward in melee).</summary>
        private const double RangedTravelSeconds = 3.0;
        private const uint MaxHealth = 10000;

        /// <summary>Whether active defenders drop their task for a newly arrived corrupted monster (the assault broadcast).</summary>
        internal static bool PreemptForCorrupted = true;

        /// <summary>How many players at most focus one corrupted monster together.</summary>
        private const int CorruptedHands = 3;

        private enum Strategy { Active, Ignore, NoFocus }

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
            public int Assaults;
            public double AssaultDamage;
            public double BossDamage;
            public int BossKills;
            public int BossSwaps;
            public int BossOnTargetSeconds;
            public int BossPulledSeconds;
            public override string ToString() =>
                $"{(Won ? "WIN " : "LOSS")} survivors={Survivors}/3 waves={WavesStarted} end={EndSeconds:0}s " +
                $"healed={HealedTotal / MaxHealth * 100:0}% dmg={DamageTotal / MaxHealth * 100:0}% above90={WavesAbove90}/{TargetWaves} assaults={Assaults} assaultDmg={AssaultDamage / MaxHealth * 100:0}%";
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

        private static Result Simulate(ProtectDef p, int holdSeconds, int players, Strategy strategy, int seed, StringBuilder trace = null)
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

            // The finale boss (protect.bossTargetsProtected): its own target, swap timer, pull, damage bucket and the player
            // damage it has taken (for the pull), as WorldEventFinaleBoss rules them.
            Monster boss = null;
            var bossNextSwap = 0.0;
            var bossPulledUntil = -1.0;
            var bossTokens = new double[3];
            var bossLastAt = new double[3];
            var bossDamageLog = new List<(double At, uint Player, double Amount)>();
            const uint BossMaxHealth = 30000;

            bool Alive(int i) => hp[i] > 0;

            List<(uint Guid, uint Current, uint Max)> TargetHealth() =>
                Enumerable.Range(0, 3).Select(i => ((uint)(i + 1), (uint)Math.Max(0, hp[i]), MaxHealth)).ToList();

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

            // An arrived corrupted monster with fewer than CorruptedHands players on it, the least-attended first.
            Monster CorruptedNeedingHands(double now) => monsters
                .Where(m => m.Alive && m.Role == HoldRole.Corrupted && now >= m.ArriveAt && playerTask.Count(x => x == m) < CorruptedHands)
                .OrderBy(m => playerTask.Count(x => x == m)).FirstOrDefault();

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
                    boss = new Monster { Role = HoldRole.Seeker, Boss = true, Target = healthiest };
                    batch.Add(boss);

                    if (p.BossTargetsProtected)
                    {
                        boss.Target = (int)WorldEventFinaleBoss.PickTarget(TargetHealth(), 0, false) - 1;
                        bossNextSwap = now + ArriveSeconds + WorldEventFinaleBoss.SwapDelay(rng, p.BossSwapSeconds, p.BossSwapMaxSeconds);
                    }

                    var rest = size + WorldEventHoldRules.FinalWaveBonus(p.WaveSize.Base);

                    for (var k = 0; k < rest; k++)
                        batch.Add(new Monster { Role = k < rest * p.SeekShare ? HoldRole.Seeker : HoldRole.Hunter });
                }
                else
                {
                    // The corrupted assault: an assault wave brings assaultCount extra, all corrupted; others none.
                    var c = p.EffectiveCorrupted;
                    var assault = c.AssaultMode && WorldEventHoldRules.IsAssaultWave(waves, c.AssaultEveryWaves, c.AssaultFromWave, false);
                    var limits = WorldEventHoldRules.AssaultMix(mix.Shares, mix.Minimums, mix.Maximums, c.AssaultMode);
                    var counts = WorldEventHoldRules.WaveMixCounts(size, limits.Shares, limits.Minimums, limits.Maximums);

                    if (assault)
                        counts[(int)HoldRole.Corrupted] += c.AssaultCount;

                    var assaultTargets = assault ? WorldEventHoldRules.AssaultTargets(aliveTargets.Count, counts[(int)HoldRole.Corrupted], rng) : null;
                    var nextAssault = 0;

                    for (var r = 0; r < counts.Length; r++)
                        for (var k = 0; k < counts[r]; k++)
                        {
                            var m = new Monster { Role = (HoldRole)r };

                            if (assault && m.Role == HoldRole.Corrupted && assaultTargets.Count > 0)
                                m.Target = aliveTargets[assaultTargets[nextAssault++ % assaultTargets.Count]];

                            batch.Add(m);
                        }

                    if (assault)
                        result.Assaults++;
                }

                foreach (var m in batch)
                {
                    m.Wave = waves;
                    m.ArriveAt = now + (m.Role == HoldRole.Runner ? 2.5 : m.Role == HoldRole.Ranged ? 2.0 : ArriveSeconds) + (m.Boss ? p.FinaleBossLeadSeconds * 0 : 0);
                    m.WorkLeft = m.Boss ? BossKillSeconds
                        : m.Role == HoldRole.Runner ? KillSeconds * 0.6
                        : m.Role == HoldRole.Corrupted ? KillSeconds * p.EffectiveCorrupted.HealthMult + CorruptedTravelSeconds
                        : m.Role == HoldRole.Ranged ? KillSeconds + RangedTravelSeconds
                        : KillSeconds;

                    m.WorkLeft *= crowd;

                    if (m.Role == HoldRole.Hunter)
                        m.Target = -1;
                    else if (m.Boss || m.Target >= 0)
                    {
                        // already set (the boss, an assault monster)
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

                // The finale boss: a pull (enough player damage in the window) holds it on a player; otherwise it swaps to
                // the healthiest other target on its timer, and takes the healthiest when its target falls.
                var bossControlled = p.BossTargetsProtected && boss != null && boss.Alive;
                var bossPulled = false;

                if (bossControlled)
                {
                    var (recent, _) = WorldEventFinaleBoss.RecentDamage(bossDamageLog, t, p.BossPullWindowSeconds);

                    if (WorldEventFinaleBoss.PullDue(recent, BossMaxHealth, p.BossPullShare))
                        bossPulledUntil = t + p.BossPullSeconds;

                    bossPulled = t < bossPulledUntil;
                    var backFromPull = !bossPulled && bossPulledUntil >= 0 && t - bossPulledUntil < 1;

                    if (!bossPulled && Enumerable.Range(0, 3).Any(Alive))
                    {
                        // Back from a pull it turns on a different target, as the controller does.
                        var swap = t >= bossNextSwap || backFromPull;
                        var current = boss.Target >= 0 && Alive(boss.Target) ? (uint)(boss.Target + 1) : 0u;
                        var pick = (int)WorldEventFinaleBoss.PickTarget(TargetHealth(), current, swap) - 1;

                        if (pick != boss.Target || swap)
                            bossNextSwap = t + WorldEventFinaleBoss.SwapDelay(rng, p.BossSwapSeconds, p.BossSwapMaxSeconds);

                        if (pick != boss.Target)
                            result.BossSwaps++;

                        boss.Target = pick;
                    }

                    // Its own damage: alone it needs bossMinKillSeconds to empty a target, and the shared floor holds it with
                    // the rest of the wave (the boss hits first in a second, the wave takes what the floor has left).
                    if (!bossPulled && t >= boss.ArriveAt && boss.Target >= 0 && Alive(boss.Target) && p.BossMinKillSeconds > 0)
                    {
                        var i = boss.Target;
                        var hit = WorldEventFinaleBoss.CapHit(WorldEventFinaleBoss.HitDamage(MaxHealth, 1.0, p.BossMinKillSeconds), MaxHealth,
                            p.BossMinKillSeconds, ref bossTokens[i], ref bossLastAt[i], 1_700_000_000d + t);

                        if (p.MinDeathSeconds > 0)
                            hit = WorldEventHoldRules.CapProtectedHit(hit, MaxHealth, p.MinDeathSeconds, ref capTokens[i], ref capLastAt[i], 1_700_000_000d + t);

                        hp[i] -= hit;
                        result.DamageTotal += hit;
                        result.BossDamage += hit;
                        lastHit[i] = t;
                        result.BossOnTargetSeconds++;

                        if (hp[i] <= 0)
                        {
                            hp[i] = 0;
                            result.BossKills++;

                            foreach (var m in monsters.Where(m => m.Alive && m.Target == i && !m.Boss))
                                m.Target = LeastAttacked(-1);
                        }
                    }

                    if (bossPulled)
                        result.BossPulledSeconds++;
                }

                // Damage: every arrived attacker, time-based, the corrupted aura, the cap.
                for (var i = 0; i < 3; i++)
                {
                    if (!Alive(i))
                        continue;

                    var on = monsters.Where(m => m.Alive && m.Target == i && t >= m.ArriveAt && !(bossControlled && m.Boss)).ToList();

                    if (on.Count == 0)
                        continue;

                    var corruptedHere = on.Any(m => m.Role == HoldRole.Corrupted);
                    var corrupted = p.EffectiveCorrupted;
                    var dmg = 0.0;
                    var assaultRate = 0.0;

                    foreach (var m in on)
                    {
                        var rate = MaxHealth / p.EffectiveUndefendedSeconds(participants);

                        if (m.Role == HoldRole.Runner)
                            rate *= p.SpecialWave(HoldWaveType.Runners)?.AttackSpeedMult ?? 1.5;

                        rate *= WorldEventHoldRules.EmpowerFactor(m.Role == HoldRole.Corrupted, corruptedHere ? 0 : (double?)null,
                            corrupted.EmpowerRadius, corrupted.EmpowerMult, corrupted.SelfMult);

                        // Every hit counts against the cap, the corrupted assault monster's included (2026-10-19).
                        dmg += rate;

                        if (m.Role == HoldRole.Corrupted && corrupted.AssaultMode)
                            assaultRate += rate;
                    }

                    var capped = p.MinDeathSeconds > 0
                        ? WorldEventHoldRules.CapProtectedHit((float)dmg, MaxHealth, p.MinDeathSeconds, ref capTokens[i], ref capLastAt[i], 1_700_000_000d + t)
                        : dmg;

                    // The assault monster's share of what the cap let through.
                    if (assaultRate > 0 && dmg > 0)
                        result.AssaultDamage += Math.Min(capped * assaultRate / dmg, hp[i]);

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

                    // Active defense answers the assault broadcast: a player on anything else drops it for a corrupted
                    // monster that has arrived and has fewer than CorruptedHands players on it (groups focus it together).
                    // The boss is only hit long enough to pull it: once it is held on a player, back to the escorts.
                    if (task != null && task.Boss && bossPulled && monsters.Any(m => m.Alive && !m.Boss && m.Target >= 0 && t >= m.ArriveAt - 1 && !playerTask.Contains(m)))
                    {
                        playerTask[pl] = task = null;
                        playerFree[pl] = t + SwitchSeconds;
                    }

                    if (PreemptForCorrupted && strategy == Strategy.Active && task != null && task.Alive && task.Role != HoldRole.Corrupted)
                    {
                        var corruptedOpen = CorruptedNeedingHands(t);

                        if (corruptedOpen != null)
                        {
                            playerTask[pl] = task = corruptedOpen;
                            playerFree[pl] = t + SwitchSeconds;
                        }
                    }

                    if (task != null && task.Alive && t < playerFree[pl])
                        continue;

                    if (task != null && task.Alive)
                    {
                        task.WorkLeft -= 1;

                        // Damage to the boss counts toward pulling it (its whole health is BossKillSeconds of one player's work).
                        if (task.Boss)
                            bossDamageLog.Add((t, (uint)(pl + 1), BossMaxHealth / (BossKillSeconds * crowd)));

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

                    if (strategy != Strategy.Ignore && p.Shrine != null)
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

                    if (strategy != Strategy.Ignore)
                    {
                        var weakest = Enumerable.Range(0, 3).Where(Alive).OrderBy(i => hp[i]).ToList();

                        // Active defense pulls the boss off a target that is under 60% (one player on it is enough to hold it).
                        var bossOnWeak = strategy != Strategy.Ignore && p.BossTargetsProtected && boss != null && boss.Alive && t >= boss.ArriveAt - 1
                                         && !bossPulled && boss.Target >= 0 && Alive(boss.Target) && hp[boss.Target] < MaxHealth * 0.6 && !playerTask.Contains(boss);

                        // NoFocus defends like Active but treats a corrupted monster as any other.
                        pick = (strategy == Strategy.Active ? CorruptedNeedingHands(t) : null)
                            ?? (bossOnWeak ? boss : null)
                            ?? weakest.Select(w => open.FirstOrDefault(m => m.Target == w && !m.Boss)).FirstOrDefault(m => m != null)
                            ?? open.FirstOrDefault(m => m.Target >= 0)
                            ?? open.FirstOrDefault();
                    }
                    else
                        pick = open.FirstOrDefault(m => m.Role == HoldRole.Hunter);

                    if (pick != null)
                        playerTask[pl] = pick;
                }

                if (trace != null && t % 5 == 0)
                    trace.AppendLine($"t={t} wave={waves} hp={hp[0]:0}/{hp[1]:0}/{hp[2]:0} alive=" + string.Join(",", monsters.Where(m => m.Alive).Select(m => $"{m.Role.ToString()[0]}{(m.Boss ? "B" : "")}->{m.Target}{(t >= m.ArriveAt ? "" : "~")}w{m.WorkLeft:0}")) + " tasks=" + string.Join(",", playerTask.Select(x => x == null ? "-" : x.Role.ToString()[0].ToString())));

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

        private static readonly int[] GroupSizes = { 1, 2, 3, 5, 10 };

        private const int Seeds = 40;

        private static string Report(ProtectDef p, int holdSeconds, out Dictionary<(int, Strategy), List<Result>> all)
        {
            all = new Dictionary<(int, Strategy), List<Result>>();
            var sb = new StringBuilder();

            foreach (var players in GroupSizes)
            {
                foreach (var strategy in new[] { Strategy.Active, Strategy.NoFocus, Strategy.Ignore })
                {
                    var runs = Enumerable.Range(0, Seeds).Select(seed => Simulate(p, holdSeconds, players, strategy, seed)).ToList();
                    all[(players, strategy)] = runs;

                    sb.AppendLine($"{players,2} player(s) {strategy,-7}: wins {runs.Count(r => r.Won)}/{Seeds}, " +
                                  $"envoys left avg {runs.Average(r => r.Survivors):0.00} (0/1/2/3: {runs.Count(r => r.Survivors == 0)}/" +
                                  $"{runs.Count(r => r.Survivors == 1)}/{runs.Count(r => r.Survivors == 2)}/{runs.Count(r => r.Survivors == 3)}), " +
                                  $"end avg {runs.Average(r => r.EndSeconds):0}s, waves {runs.Average(r => r.WavesStarted):0.0}, " +
                                  $"assaults {runs.Average(r => r.Assaults):0.0}, target-waves above 90% {runs.Sum(r => r.WavesAbove90)}/{runs.Sum(r => r.TargetWaves)}, " +
                                  $"boss: dmg {runs.Average(r => r.BossDamage) / MaxHealth * 100:0}% on targets {runs.Average(r => r.BossOnTargetSeconds):0}s " +
                                  $"pulled {runs.Average(r => r.BossPulledSeconds):0}s swaps {runs.Average(r => r.BossSwaps):0.0} kills {runs.Average(r => r.BossKills):0.00}");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// The owner's targets, on the shipped numbers: active defense (solo up to 10 players) never loses every envoy and
        /// keeps at least 1.5 on average; ignoring the envoys always loses; defending without focusing the corrupted
        /// assault lets it land at least twice the damage; and healing never makes them untouchable (at most one
        /// envoy-wave per run spent wholly above 90%, the opening wave's quiet side).
        /// </summary>
        [TestMethod]
        public void Balance_ActiveDefenseHolds_IgnoringLoses_TheAssaultMustBeFocused()
        {
            var p = ShippedEnvoys(out var holdSeconds);
            var report = Report(p, holdSeconds, out var all);

            Console.WriteLine(report);

            foreach (var players in GroupSizes)
            {
                var active = all[(players, Strategy.Active)];
                var noFocus = all[(players, Strategy.NoFocus)];
                var ignore = all[(players, Strategy.Ignore)];

                Assert.IsTrue(active.All(r => r.Won && r.Survivors >= 1), $"{players} player(s), active defense keeps at least one envoy alive\n{report}");
                Assert.IsTrue(active.Average(r => r.Survivors) >= 1.5, $"{players} player(s): active defense keeps 1.5 envoys or more on average\n{report}");
                Assert.IsTrue(ignore.All(r => !r.Won), $"{players} player(s), ignoring the envoys loses\n{report}");
                Assert.IsTrue(active.All(r => r.Assaults >= 2), $"{players} player(s): the corrupted assault comes at least twice\n{report}");
                Assert.IsTrue(active.All(r => r.WavesAbove90 <= 1), $"{players} player(s): no envoy idles above 90% beyond one wave\n{report}");

                // Focusing the corrupted assault matters at every group size. Since 2026-10-19 its hits count against
                // minDeathSeconds, so it no longer kills an envoy on its own: an unfocused one lands at least twice the damage.
                Assert.IsTrue(noFocus.Average(r => r.AssaultDamage) >= 2 * active.Average(r => r.AssaultDamage),
                    $"{players} player(s): ignoring the corrupted assault lets it land far more damage\n{report}");
            }
        }

        /// <summary>
        /// The model against the live solo runs, the hardest case (no shrine). The runs of 2026-10-18 20:16 and 2026-10-19 11:57
        /// each lost envoys only because the corrupted assault skipped minDeathSeconds (fixed 2026-10-19). With the floor held,
        /// solo active defense keeps every envoy but is no walkover: the weakest ends well below full health.
        /// </summary>
        [TestMethod]
        public void Balance_ModelReproducesTheLiveSoloRun()
        {
            var p = ShippedEnvoys(out var holdSeconds);
            p.Shrine = null;

            var runs = Enumerable.Range(0, 60).Select(seed => Simulate(p, holdSeconds, 1, Strategy.Active, seed)).ToList();
            var avg = runs.Average(r => r.Survivors);
            var lowest = runs.Average(r => r.LowestEndHealth);

            Assert.IsTrue(avg >= 1.0, $"solo, no shrine: {avg:0.00} envoys left on average");
            Assert.IsTrue(lowest <= 0.6, $"solo, no shrine: the weakest envoy ends at {lowest * 100:0}% on average, so the fight still bites");
        }

        /// <summary>
        /// The finale boss in the model (protect.bossTargetsProtected): with active defense it threatens the envoys (it lands
        /// damage on them and swaps between them) but the finale still ends with at least one envoy alive, at every group size.
        /// </summary>
        [TestMethod]
        public void Balance_FinaleBossThreatensTheEnvoysButActiveDefenseKeepsOne()
        {
            var p = ShippedEnvoys(out var holdSeconds);

            Assert.IsTrue(p.BossTargetsProtected, "the envoys' boss goes for the envoys");

            var sb = new StringBuilder();

            foreach (var players in GroupSizes)
            {
                var runs = Enumerable.Range(0, Seeds).Select(seed => Simulate(p, holdSeconds, players, Strategy.Active, seed)).ToList();

                sb.AppendLine($"{players} player(s): boss dmg {runs.Average(r => r.BossDamage) / MaxHealth * 100:0}%, on targets {runs.Average(r => r.BossOnTargetSeconds):0} s, " +
                              $"pulled {runs.Average(r => r.BossPulledSeconds):0} s, swaps {runs.Average(r => r.BossSwaps):0.0}, kills {runs.Average(r => r.BossKills):0.00}, " +
                              $"envoys left {runs.Average(r => r.Survivors):0.00}");

                Assert.IsTrue(runs.All(r => r.Survivors >= 1), $"{players} player(s): the finale ends with an envoy alive\n{sb}");
                Assert.IsTrue(runs.Average(r => r.BossDamage) > 0.05 * MaxHealth, $"{players} player(s): the boss threatens the envoys\n{sb}");
            }

            Console.WriteLine(sb);
        }

        /// <summary>The boss alone, hitting as fast as it likes, needs bossMinKillSeconds to take a full target to 0.</summary>
        [TestMethod]
        public void FinaleBoss_AloneNeedsAtLeastMinKillSeconds()
        {
            foreach (var interval in new[] { 0.25, 0.5, 1.0, 2.0, 3.5 })
            {
                double tokens = 0, lastAt = 0, health = MaxHealth, now = 1_700_000_000d, last = 0;
                double? prev = null;

                while (health > 0)
                {
                    var hit = WorldEventFinaleBoss.CapHit(WorldEventFinaleBoss.HitDamage(MaxHealth, WorldEventHoldRules.HitInterval(prev, now), 50), MaxHealth, 50,
                        ref tokens, ref lastAt, now);

                    prev = now;
                    health -= hit;
                    last = now;
                    now += interval;
                }

                Assert.IsTrue(last - 1_700_000_000d >= 50 - 2.0 - interval, $"hits every {interval} s: down in {last - 1_700_000_000d:0.0} s");
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
