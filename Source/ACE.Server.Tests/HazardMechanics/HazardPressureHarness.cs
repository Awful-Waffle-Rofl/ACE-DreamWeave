using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

using ACE.Entity;
using ACE.Server.HazardMechanics;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>How a simulated player moves while a hazard runs.</summary>
    public enum PressureBehaviour
    {
        /// <summary>S: stands on its home spot and never dodges.</summary>
        Stationary,

        /// <summary>D: PERFECT dodger (an upper bound): reacts ReactionSeconds after every telegraph, knows its exact reach, moves at the dodge speed to the nearest clear spot, returns home when clear.</summary>
        Dodger,

        /// <summary>D50: the dodger degraded: reacts DegradedReactionSeconds late and acts on only DegradedAttention of the telegraphs (a seeded draw per player per telegraph).</summary>
        DodgerDegraded,

        /// <summary>K: runs a continuous circle round the arena centre at KiteSpeed.</summary>
        Kiter,
    }

    /// <summary>One measurement: a hazard kind and its settings row, a group, a behaviour and a live count. A record: <see cref="WithSeed"/> copies every other property.</summary>
    public sealed record PressureConfig
    {
        /// <summary>wisps | balls | rain.</summary>
        public string Kind { get; init; }

        /// <summary>The world_events_hazard_&lt;kind&gt; row (the settings layer HazardManager.Start lays the start args over). Null = the shipped World Event default.</summary>
        public string Args { get; init; }

        /// <summary>Balls only: pin this pattern. Null = what BuildStart picks from the plan's pool, per run, as a World Event does.</summary>
        public string Pattern { get; init; }

        /// <summary>
        /// Balls only: run the World Event's EVENT-LEVEL hazard: a source-default balls plan alternates rolling and rain per run
        /// (WorldEventHazards.NextRunKind), each rolling run picking its pattern from the pool. <see cref="Pattern"/> is ignored.
        /// </summary>
        public bool EventLevel { get; init; }

        /// <summary>The rain row an event-level balls run uses for its rain halves. Null = the shipped default.</summary>
        public string RainArgs { get; init; }

        public int Players { get; init; } = 1;

        public PressureBehaviour Behaviour { get; init; }

        /// <summary>The run's live count (WorldEventHazards.CountAt output; 4 = ramp low, 10 = ramp high).</summary>
        public int LiveCount { get; init; } = WorldEventHazardSettings.DefaultCountLow;

        public double Seconds { get; init; } = 300.0;

        public int Seed { get; init; } = 1;

        public double Pct { get; init; } = WorldEventHazardSettings.DefaultPct;

        /// <summary>The theme's rewardRadius (Content/events/axes/sources.json: 60); the arena comes from WorldEvent.BuildArena with it.</summary>
        public float ArenaRadius { get; init; } = 60f;

        /// <summary>Players' home spots are uniform in a disc of this radius round the arena centre (an assumption: the crowd stays near the fight).</summary>
        public float CrowdRadius { get; init; } = 20f;

        public uint MaxHealth { get; init; } = 1000;

        public double ReactionSeconds { get; init; } = 0.5;

        public double DegradedReactionSeconds { get; init; } = 1.5;

        /// <summary>The share of telegraphs the degraded dodger acts on.</summary>
        public double DegradedAttention { get; init; } = 0.5;

        /// <summary>The dodgers' speed. Null = the run's merged escape_speed when its row has one, else HazardDefaults.EscapeSpeed (the speed every fair-warning rule assumes).</summary>
        public double? DodgeSpeed { get; init; }

        public double KiteSpeed { get; init; } = 4.0;

        /// <summary>Extra metres a dodger keeps between itself and a threat's reach.</summary>
        public double DodgeMargin { get; init; } = 0.5;

        /// <summary>Seconds from one run's end to the next run's start (the World Event settles an end and chains on its own tick, so up to one tick).</summary>
        public double ChainGapSeconds { get; init; } = 1.0;

        public PressureConfig WithSeed(int seed) => this with { Seed = seed };
    }

    /// <summary>The result of one seeded run of <see cref="HazardPressureHarness.Run"/>.</summary>
    public sealed class PressureRun
    {
        public int Hits { get; init; }

        /// <summary>Sum over every hit of the damage dealt, in percent of max health.</summary>
        public double PctLost { get; init; }

        /// <summary>Each player's own total (percent of max health), by player index. Sums to <see cref="PctLost"/>.</summary>
        public IReadOnlyList<double> PlayerPct { get; init; }

        public double Seconds { get; init; }

        public double PlayerMinutes => PlayerPct.Count * Seconds / 60.0;

        public double PctPerPlayerMinute => PlayerMinutes > 0 ? PctLost / PlayerMinutes : 0.0;

        public double HitsPerPlayerMinute => PlayerMinutes > 0 ? Hits / PlayerMinutes : 0.0;

        /// <summary>The worst-off player's %maxHP per minute.</summary>
        public double MaxPlayerPctPerMinute => Seconds > 0 ? PlayerPct.Max() * 60.0 / Seconds : 0.0;

        /// <summary>The 90th percentile player's %maxHP per minute (nearest rank).</summary>
        public double P90PlayerPctPerMinute => Seconds > 0 ? Percentile(PlayerPct, 0.9) * 60.0 / Seconds : 0.0;

        /// <summary>Hazard runs started (a Completed run chains the next, as WorldEvent.TickHazards does).</summary>
        public int RunsStarted { get; init; }

        public int RunsAborted { get; init; }

        /// <summary>Event-level balls: how many of the runs were rain.</summary>
        public int RainRuns { get; init; }

        /// <summary>Hazard events over the whole run: rain drops launched, wisp pillars fired, or balls spawned.</summary>
        public int Events { get; init; }

        public double EventsPerMinute => Seconds > 0 ? Events * 60.0 / Seconds : 0.0;

        public IReadOnlyList<string> Patterns { get; init; }

        internal static double Percentile(IReadOnlyList<double> v, double q)
        {
            var sorted = v.OrderBy(x => x).ToList();
            var rank = (int)Math.Ceiling(q * sorted.Count);
            return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
        }
    }

    /// <summary>Several seeds of one config.</summary>
    public sealed class PressureSummary
    {
        public PressureConfig Config { get; init; }

        public IReadOnlyList<PressureRun> Runs { get; init; }

        public double MeanPct => Runs.Average(r => r.PctPerPlayerMinute);

        /// <summary>Sample standard deviation over the seeds of %maxHP per player-minute.</summary>
        public double SdPct => Sd(Runs.Select(r => r.PctPerPlayerMinute).ToList());

        /// <summary>Standard error of <see cref="MeanPct"/>.</summary>
        public double SePct => Runs.Count > 1 ? SdPct / Math.Sqrt(Runs.Count) : 0.0;

        public double MeanHits => Runs.Average(r => r.HitsPerPlayerMinute);

        /// <summary>The worst-off player's %maxHP per minute, averaged over the seeds.</summary>
        public double MeanMaxPct => Runs.Average(r => r.MaxPlayerPctPerMinute);

        public double MeanP90Pct => Runs.Average(r => r.P90PlayerPctPerMinute);

        public double EventsPerMinute => Runs.Average(r => r.EventsPerMinute);

        private static double Sd(IReadOnlyList<double> v)
        {
            if (v.Count < 2)
                return 0.0;

            var m = v.Average();
            return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
        }
    }

    /// <summary>
    /// The damage ledger of HazardContext.HitPlayers, for points instead of players: a player is hit when
    /// InReach(centre, player, radius, playerRadius) and inside the region, at most once per window per KEY, where the key is
    /// (hitGroup ?? sourceKey, player), exactly as HitPlayers keys its lastHits. HazardPressureTests proves the parity against
    /// the real HitPlayers on one randomised strike sequence. <see cref="DropHitGroup"/> is a mutation switch for that proof.
    /// </summary>
    public sealed class PressureLedger
    {
        private readonly Dictionary<(long Key, int Player), double> lastHits = new Dictionary<(long, int), double>();

        /// <summary>Mutation switch (tests only): key by the source alone, ignoring the hit group.</summary>
        public bool DropHitGroup { get; set; }

        public void Clear() => lastHits.Clear();

        /// <summary>Returns how many players were hit; <paramref name="onHit"/> gets each hit player's index.</summary>
        public int Strike(long sourceKey, int? hitGroup, Vector3 centre, float radius, double window, double now,
            IReadOnlyList<Vector3> players, double playerRadius, Func<Vector3, bool> inside, Action<int> onHit)
        {
            var hits = 0;
            long key = DropHitGroup ? sourceKey : (hitGroup ?? sourceKey);

            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];

                if (!HazardHitRules.InReach(centre, p, radius, playerRadius))
                    continue;

                if (inside != null && !inside(p))
                    continue;

                var k = (key, i);
                double? last = lastHits.TryGetValue(k, out var t) ? t : (double?)null;

                if (!HazardHitRules.WindowOpen(last, now, window))
                    continue;

                lastHits[k] = now;
                hits++;
                onHit?.Invoke(i);
            }

            return hits;
        }
    }

    /// <summary>
    /// HAZARD PRESSURE: % of max health lost per player-minute under a World Event hazard, for a group of simulated players,
    /// with the worst-off player's share alongside the mean (a follower or an aimed drop concentrates on one player).
    ///
    /// WHAT IS REAL. The arena is WorldEvent.BuildArena on a theme with the configured rewardRadius; the run is planned by the
    /// real WorldEventHazards.Plan / BuildStart / NextRunKind (region, element args, cycles, drop_z, the ball pattern pool and
    /// pick, the rolling-or-rain alternation); the args are merged as HazardManager.Start merges them and each start passes
    /// the mechanic's own Validate; the damage is HazardHitRules.DamageFor with ctx.Pct and ctx.PlayerRadius read from the
    /// merged args by the same expressions HazardContext uses, through <see cref="PressureLedger"/> (HitPlayers' keying,
    /// parity-tested). Rain and wisps are the REAL RainDirector / WispDirector, driven through fake ports on the live 100 ms
    /// tick (HazardDefaults.TickMs), with the scheduled steps run before the director tick as HazardManager.TickRun does.
    ///
    /// WHAT IS APPROXIMATE. Balls have no port, so their run is a geometric re-hosting of RollingBallMechanic's CADENCE mode
    /// from its pure parts: BallRules.Read / ForWave / CapToLiveCount / LeadFor / LegSeconds, BallCadence (launch times and the
    /// live-count ball budget), BallPatterns.Plan and PlanFanAlong, BallWaveClock and BallHits (the lagged sweep). The shell
    /// code between them is copied in shape, and HazardPressureTests pins each copied expression against the mechanic's source.
    /// Everywhere: a flat plane with a cell at every point, nothing fails to spawn, a wisp reaches its glide end at once,
    /// players never die, heal or report late, and the dodgers' policy (nearest clear spot on rings round them, ignoring what
    /// they cross on the way) is this harness's own.
    /// </summary>
    public static class HazardPressureHarness
    {
        public const string Wisps = WorldEventHazards.KindWisps;
        public const string Balls = WorldEventHazards.KindBalls;
        public const string Rain = WorldEventHazards.KindRain;

        /// <summary>The live tick, seconds.</summary>
        public static readonly double Dt = HazardDefaults.TickMs / 1000.0;

        private const float ArenaX = 96f;
        private const float ArenaY = 96f;
        private const float GroundZ = 50f;

        /// <summary>The shipped World Event row for a kind.</summary>
        public static string DefaultArgs(string kind)
        {
            switch (kind)
            {
                case Wisps: return WorldEventHazardSettings.DefaultWisps;
                case Balls: return WorldEventHazardSettings.DefaultBalls;
                case Rain: return WorldEventHazardSettings.DefaultRain;
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a world event hazard kind");
            }
        }

        /// <summary>
        /// <paramref name="overlays"/> laid in turn over the shipped World Event row of <paramref name="kind"/> (a later overlay
        /// wins; within one string a repeated key keeps its FIRST value, as HazardArgs.Parse does), as one row string.
        /// </summary>
        public static string Overlay(string kind, params string[] overlays)
        {
            var merged = HazardArgs.Parse(DefaultArgs(kind));

            foreach (var o in overlays)
                merged = HazardArgs.Merge(HazardArgs.Parse(o), merged);

            return merged.ToString();
        }

        private static void CheckRow(string kind, string row)
        {
            var errors = new List<string>();
            HazardArgs.Parse(row, errors);

            if (errors.Count > 0)
                throw new ArgumentException($"{kind} row [{row}]: {string.Join("; ", errors)}");
        }

        /// <summary>The arena a config runs in: WorldEvent.BuildArena on a theme whose rewardRadius is <see cref="PressureConfig.ArenaRadius"/>.</summary>
        public static WorldEventArena ArenaFor(PressureConfig c)
            => WorldEvent.BuildArena(new SourceThemeDef { Id = "pressure", RewardRadius = c.ArenaRadius },
                new Position(0xA9B40005, ArenaX, ArenaY, GroundZ, 0f, 0f, 0f, 1f, 0));

        /// <summary>The World Event plan a config runs under (settings rows, pct, arena), from the real planner.</summary>
        public static WorldEventHazardPlan PlanFor(PressureConfig c, Random rng)
        {
            CheckRow(c.Kind, c.Args);
            CheckRow(Rain, c.RainArgs);

            if (c.EventLevel && c.Kind != Balls)
                throw new ArgumentException("EventLevel is a balls plan only (balls alternate with rain)");

            var d = WorldEventHazardSettings.Defaults;
            var settings = d with
            {
                Pct = c.Pct,
                Wisps = c.Kind == Wisps && c.Args != null ? c.Args : d.Wisps,
                Balls = c.Kind == Balls && c.Args != null ? c.Args : d.Balls,
                Rain = c.Kind == Rain && c.Args != null ? c.Args : c.RainArgs ?? d.Rain,
            };

            var arena = ArenaFor(c) ?? throw new InvalidOperationException("WorldEvent.BuildArena built no arena");
            var source = new SourceThemeDef { Id = "pressure", Hazard = new HazardDef { Kind = c.Kind }, Decor = new List<DecorDef>() };

            // the event level: the SOURCE default "balls" (no request), which alternates rolling and rain; else an explicit request
            var plan = WorldEventHazards.Plan(source, c.EventLevel ? null : c.Kind, settings, arena, rng, out var note);

            if (plan == null)
                throw new InvalidOperationException($"the planner refused {c.Kind}: {note}");

            if (plan.Kind != c.Kind)
                throw new InvalidOperationException($"the planner turned {c.Kind} into {plan.Kind}: {note}");

            if (c.EventLevel && !plan.BallsAlternateWithRain)
                throw new InvalidOperationException("the event-level balls plan does not alternate with rain");

            return plan;
        }

        /// <summary>The ball patterns a World Event with this row may roll (the plan's pool).</summary>
        public static IReadOnlyList<string> BallPool(string args = null)
            => PlanFor(new PressureConfig { Kind = Balls, Args = args }, new Random(1)).BallPatterns;

        public static PressureSummary Measure(PressureConfig c, int seeds = 5)
        {
            if (seeds < 1)
                throw new ArgumentOutOfRangeException(nameof(seeds));

            var runs = new List<PressureRun>();

            for (var s = 1; s <= seeds; s++)
                runs.Add(Run(c.WithSeed(c.Seed * 1000 + s)));

            return new PressureSummary { Config = c, Runs = runs };
        }

        /// <summary>One seeded run: <see cref="PressureConfig.Seconds"/> of hazard (runs chained on Completed), every player exposed throughout.</summary>
        public static PressureRun Run(PressureConfig c)
        {
            if (c.Players < 1)
                throw new ArgumentOutOfRangeException(nameof(c), "Players must be at least 1");

            var world = new World(c);
            world.RunAll();

            return new PressureRun
            {
                Hits = world.Hits,
                PctLost = world.PctLost,
                PlayerPct = world.Agents.Select(a => a.PctLost).ToList(),
                Seconds = c.Seconds,
                RunsStarted = world.RunsStarted,
                RunsAborted = world.RunsAborted,
                RainRuns = world.RainRuns,
                Events = world.Events,
                Patterns = world.Patterns,
            };
        }

        // =============================================================================================================
        // the world: players, scheduler, ledger, threats
        // =============================================================================================================

        internal sealed class Handle
        {
            public int Id;
            public Vector3 Pos;
            public bool Dead;

            /// <summary>Timed effects: alive while the judged time is before this. 0 = until Dead.</summary>
            public double Until;

            public Threat Threat;
        }

        /// <summary>Something a dodger can see coming. Times are absolute (player) seconds.</summary>
        internal abstract class Threat
        {
            public int Id;
            public double SpawnAt;
            public double Until;
            public float Reach;
            public bool Removed;

            /// <summary>Would a player standing still at <paramref name="p"/> from <paramref name="now"/> on be within reach + margin?</summary>
            public abstract bool Covers(Vector2 p, double now, double margin);
        }

        internal sealed class StaticThreat : Threat
        {
            public Vector2 Centre;

            public override bool Covers(Vector2 p, double now, double margin)
                => Vector2.DistanceSquared(p, Centre) < (Reach + margin) * (Reach + margin);
        }

        /// <summary>A ball waiting its lead at From, then rolling From -> To from RollAt over LegSeconds.</summary>
        internal sealed class LaneThreat : Threat
        {
            public Vector2 From;
            public Vector2 To;
            public double RollAt;
            public double LegSeconds;

            public override bool Covers(Vector2 p, double now, double margin)
            {
                var f = now <= RollAt ? 0.0 : Math.Min(1.0, (now - RollAt) / Math.Max(1e-6, LegSeconds));
                var a = Vector2.Lerp(From, To, (float)f);
                return DistanceToSegment(p, a, To) < Reach + margin;
            }
        }

        internal static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var len2 = ab.LengthSquared();

            if (len2 < 1e-9f)
                return Vector2.Distance(p, a);

            var t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
            return Vector2.Distance(p, a + ab * t);
        }

        internal sealed class Agent
        {
            public int Index;
            public uint Guid;
            public Vector2 Home;
            public Vector2 Pos;
            public Vector2? Target;
            public double KiteRadius;
            public double KiteAngle;
            public int KiteSign;
            public double PctLost;
        }

        private sealed class World
        {
            public readonly PressureConfig C;
            public readonly Random Rng;
            public readonly HazardRegion Region;
            public readonly WorldEventHazardPlan Plan;
            public readonly List<Agent> Agents = new List<Agent>();
            public readonly List<Threat> Threats = new List<Threat>();
            public readonly List<string> Patterns = new List<string>();
            private readonly PressureLedger ledger = new PressureLedger();

            private readonly List<(double At, int Seq, Action Step)> schedule = new List<(double, int, Action)>();
            private int scheduleSeq;
            private double now;
            private double? aliveAt;
            private int nextId;
            private int nextGroup;
            private int nextThreat;

            public int Hits;
            public double PctLost;
            public int RunsStarted;
            public int RunsAborted;
            public int RainRuns;
            private int finishedEvents;

            public int Events => finishedEvents + (runner?.Events ?? 0);

            private IRunner runner;
            private int abortStreak;
            private bool given;
            private double nextStartAt;

            public World(PressureConfig c)
            {
                C = c;
                Rng = new Random(c.Seed);
                Plan = PlanFor(c, new Random(c.Seed ^ 0x5bd1e995));
                Region = Plan.Region;

                // homes from their own stream, so the hazard's stream is the same whatever the behaviour
                var homeRng = new Random(c.Seed * 7919 + 17);

                for (var i = 0; i < c.Players; i++)
                {
                    var r = c.CrowdRadius * Math.Sqrt(homeRng.NextDouble());
                    var a = 2.0 * Math.PI * homeRng.NextDouble();
                    var home = Region.Center + new Vector2((float)(r * Math.Cos(a)), (float)(r * Math.Sin(a)));

                    var agent = new Agent { Index = i, Guid = 0x50000001u + (uint)i, Home = home, Pos = home };

                    if (c.Behaviour == PressureBehaviour.Kiter)
                    {
                        agent.KiteRadius = Math.Clamp(r, 6.0, c.ArenaRadius - 3.0);
                        agent.KiteAngle = a;
                        agent.KiteSign = i % 2 == 0 ? 1 : -1;
                        agent.Pos = KitePoint(agent);
                    }

                    Agents.Add(agent);
                }
            }

            public double Now
            {
                get => now;
                set
                {
                    now = value;
                    RunDue();
                }
            }

            public double JudgedNow => aliveAt ?? now;

            /// <summary>Moves the run clock without running the due steps (the motion half of a tick).</summary>
            public void SetClock(double value) => now = value;

            public void Schedule(double seconds, Action step) => schedule.Add((now + Math.Max(0.0, seconds), ++scheduleSeq, step));

            private void RunDue()
            {
                while (true)
                {
                    var any = false;
                    (double At, int Seq, Action Step) due = default;

                    foreach (var e in schedule)
                    {
                        if (e.At > now + 1e-9)
                            continue;

                        if (!any || e.At < due.At || (e.At == due.At && e.Seq < due.Seq))
                        {
                            due = e;
                            any = true;
                        }
                    }

                    if (!any)
                        return;

                    schedule.RemoveAll(e => e.Seq == due.Seq);
                    aliveAt = due.At;

                    try
                    {
                        due.Step();
                    }
                    finally
                    {
                        aliveAt = null;
                    }
                }
            }

            public Handle NewHandle(Vector3 pos, double until = 0.0) => new Handle { Id = ++nextId, Pos = pos, Until = until };

            public bool Alive(Handle h) => h != null && !h.Dead && (h.Until <= 0.0 || JudgedNow < h.Until);

            public void Destroy(Handle h)
            {
                if (h == null)
                    return;

                h.Dead = true;

                if (h.Threat != null)
                    h.Threat.Removed = true;
            }

            /// <summary>HazardContext.NewHitGroup: a key never equal to a handle's own.</summary>
            public int NewHitGroup() => -(++nextGroup);

            public Vector3 Point3(float x, float y, float dz = 0f) => new Vector3(x, y, GroundZ + dz);

            public IReadOnlyList<Vector3> PlayerPositions()
                => Agents.Where(a => Region.Contains(a.Pos.X, a.Pos.Y)).Select(a => Point3(a.Pos.X, a.Pos.Y)).ToList();

            public IReadOnlyList<Agent> PlayersInRegion() => Agents.Where(a => Region.Contains(a.Pos.X, a.Pos.Y)).ToList();

            private double ArgsPlayerRadius(HazardArgs args)
                => args.GetDouble(HazardDefaults.PlayerRadiusKey, HazardDefaults.PlayerRadius, HazardDefaults.MinPlayerRadius, HazardDefaults.MaxPlayerRadius);

            public float PlayerRadius => (float)ArgsPlayerRadius(runner.Args);

            /// <summary>HazardContext.HitPlayers on the merged args (ctx.Pct, ctx.PlayerRadius), HitsRequireInside, through the ledger.</summary>
            public int Strike(long sourceKey, int? hitGroup, Vector3 centre, float radius, double window)
            {
                var args = runner.Args;
                var pct = args.GetDouble(HazardDefaults.PctKey, HazardDefaults.DamagePct, HazardDefaults.MinPct, HazardDefaults.MaxPct);

                if (!(pct > 0.0))
                    return 0;

                var dealt = HazardHitRules.DamageFor(C.MaxHealth, pct);

                if (dealt == 0)
                    return 0;

                var share = 100.0 * dealt / C.MaxHealth;
                var positions = Agents.Select(a => Point3(a.Pos.X, a.Pos.Y)).ToList();

                return ledger.Strike(sourceKey, hitGroup, centre, radius, window, now, positions, ArgsPlayerRadius(args),
                    Region.HitsRequireInside ? p => Region.Contains(p.X, p.Y) : null,
                    i =>
                    {
                        Hits++;
                        PctLost += share;
                        Agents[i].PctLost += share;
                    });
            }

            public void AddThreat(Threat t)
            {
                if (C.Behaviour == PressureBehaviour.Dodger || C.Behaviour == PressureBehaviour.DodgerDegraded)
                {
                    t.Id = ++nextThreat;
                    Threats.Add(t);
                }
            }

            // ---- the loop ---------------------------------------------------------------------------------------

            public void RunAll()
            {
                var ticks = (int)Math.Round(C.Seconds / Dt);

                for (var i = 0; i <= ticks; i++)
                {
                    var t = Math.Round(i * Dt, 6);

                    // players move first (their position reports), then the hazard ticks on them
                    if (i > 0)
                        MovePlayers(t);

                    TickHazard(t);
                }
            }

            private void TickHazard(double t)
            {
                if (given)
                    return;

                if (runner == null)
                {
                    if (t + 1e-9 >= nextStartAt)
                        StartRun(t);

                    return; // HazardManager.TickRun: the Start tick does nothing else
                }

                runner.Tick(t);

                if (runner.Outcome is HazardOutcome outcome)
                {
                    finishedEvents += runner.Events;
                    runner = null;
                    nextStartAt = t + C.ChainGapSeconds;

                    if (outcome == HazardOutcome.Completed)
                        abortStreak = 0;
                    else
                    {
                        RunsAborted++;

                        if (++abortStreak > WorldEventHazards.MaxConsecutiveAborts)
                            given = true;
                    }
                }
            }

            private void StartRun(double t)
            {
                // WorldEvent.StartHazard: the run kind first (an alternating balls plan picks rolling or rain), then BuildStart
                var kind = WorldEventHazards.NextRunKind(Plan, Rng);
                var start = WorldEventHazards.BuildStart(Plan, kind, WorldEventHazardElement.Fire, GroundZ, Rng, null);
                var merged = HazardArgs.Merge(start.Args, start.SettingsLayer);
                var pattern = kind == Balls && !C.EventLevel && C.Pattern != null ? C.Pattern : start.Pattern;

                if (!HazardMechanicRegistry.TryCreate(kind, out var mechanic))
                    throw new InvalidOperationException("no mechanic " + kind);

                var error = mechanic.Validate(Region, merged, pattern);

                if (error != null)
                    throw new InvalidOperationException($"{kind} [{merged}] pattern={pattern ?? "-"} refused: {error}");

                // a fresh run: a fresh context ledger and clock (run-relative seconds), as HazardManager.Start gives
                ledger.Clear();
                schedule.Clear();
                RunsStarted++;
                Patterns.Add(kind == Balls ? pattern ?? "-" : kind);

                switch (kind)
                {
                    case Rain: runner = new RainRunner(this, merged); RainRuns++; break;
                    case Wisps: runner = new WispRunner(this, merged, pattern); break;
                    case Balls: runner = new BallRunner(this, merged, pattern); break;
                    default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a world event hazard kind");
                }

                runner.Start(t);
            }

            // ---- players ----------------------------------------------------------------------------------------

            private Vector2 KitePoint(Agent a)
                => Region.Center + new Vector2((float)(a.KiteRadius * Math.Cos(a.KiteAngle)), (float)(a.KiteRadius * Math.Sin(a.KiteAngle)));

            /// <summary>The dodgers' speed: the config's, else the live run's escape_speed when its row has one, else the fair-warning default.</summary>
            private double DodgeSpeed
            {
                get
                {
                    if (C.DodgeSpeed.HasValue)
                        return C.DodgeSpeed.Value;

                    var args = runner?.Args;

                    return args != null && args.Has(HazardDefaults.EscapeSpeedKey)
                        ? args.GetDouble(HazardDefaults.EscapeSpeedKey, HazardDefaults.EscapeSpeed, HazardDefaults.MinEscapeSpeed, HazardDefaults.MaxEscapeSpeed)
                        : HazardDefaults.EscapeSpeed;
                }
            }

            /// <summary>Does this degraded dodger act on this telegraph? A seeded draw per (seed, telegraph, player).</summary>
            private bool Attends(Agent a, Threat x)
            {
                unchecked
                {
                    var h = (uint)C.Seed * 0x9E3779B1u ^ (uint)x.Id * 0x85EBCA77u ^ (uint)(a.Index + 1) * 0xC2B2AE3Du;
                    h ^= h >> 15;
                    h *= 0x2C1B3C6Du;
                    h ^= h >> 12;
                    h *= 0x297A2D39u;
                    h ^= h >> 15;
                    return h / 4294967296.0 < C.DegradedAttention;
                }
            }

            private void MovePlayers(double t)
            {
                switch (C.Behaviour)
                {
                    case PressureBehaviour.Stationary:
                        return;

                    case PressureBehaviour.Kiter:
                        foreach (var a in Agents)
                        {
                            a.KiteAngle += a.KiteSign * C.KiteSpeed / a.KiteRadius * Dt;
                            a.Pos = KitePoint(a);
                        }

                        return;

                    default:
                        Threats.RemoveAll(x => x.Removed || x.Until <= t);

                        var degraded = C.Behaviour == PressureBehaviour.DodgerDegraded;
                        var reaction = degraded ? C.DegradedReactionSeconds : C.ReactionSeconds;
                        var known = Threats.Where(x => t >= x.SpawnAt + reaction && t < x.Until).ToList();
                        var speed = DodgeSpeed;

                        foreach (var a in Agents)
                            Dodge(a, degraded ? known.Where(x => Attends(a, x)).ToList() : known, t, speed);

                        return;
                }
            }

            private static bool Threatened(Vector2 p, List<Threat> active, double t, double margin)
            {
                foreach (var x in active)
                    if (x.Covers(p, t, margin))
                        return true;

                return false;
            }

            private void Dodge(Agent a, List<Threat> active, double t, double speed)
            {
                var step = (float)(speed * Dt);
                var margin = C.DodgeMargin;

                if (Threatened(a.Pos, active, t, margin))
                {
                    if (a.Target == null || Threatened(a.Target.Value, active, t, margin))
                        a.Target = ClearSpot(a, active, t, margin, speed);

                    if (a.Target != null)
                        a.Pos = StepToward(a.Pos, a.Target.Value, step);

                    return;
                }

                a.Target = null;

                if (Vector2.DistanceSquared(a.Pos, a.Home) < 1e-6f)
                    return;

                var next = StepToward(a.Pos, a.Home, step);

                if (!Threatened(next, active, t, margin))
                    a.Pos = next;
            }

            /// <summary>The nearest clear spot on rings of 1 m .. 20 m round the player, 16 bearings each, inside the arena by 1 m; ties go to the one nearer home.</summary>
            private Vector2? ClearSpot(Agent a, List<Threat> active, double t, double margin, double speed)
            {
                for (var r = 1; r <= 20; r++)
                {
                    Vector2? best = null;
                    var bestHome = float.MaxValue;

                    for (var k = 0; k < 16; k++)
                    {
                        var ang = 2.0 * Math.PI * k / 16.0;
                        var c = a.Pos + new Vector2((float)(r * Math.Cos(ang)), (float)(r * Math.Sin(ang)));

                        if (!Region.Contains(c.X, c.Y) || Region.DistanceToEdge(c.X, c.Y) < 1f)
                            continue;

                        // where the threats will be when the player gets there
                        if (Threatened(c, active, t + r / speed, margin))
                            continue;

                        var dh = Vector2.DistanceSquared(c, a.Home);

                        if (dh < bestHome)
                        {
                            bestHome = dh;
                            best = c;
                        }
                    }

                    if (best != null)
                        return best;
                }

                return null;
            }

            private static Vector2 StepToward(Vector2 from, Vector2 to, float step)
            {
                var d = to - from;
                var len = d.Length();
                return len <= step ? to : from + d / len * step;
            }
        }

        // =============================================================================================================
        // the runners: one hazard run each. Run clocks are run-relative (HazardContext.Now); threats are absolute (t0 + Now).
        // =============================================================================================================

        private interface IRunner
        {
            HazardArgs Args { get; }

            HazardOutcome? Outcome { get; }

            /// <summary>What the run put out: rain drops launched, wisp pillars, balls spawned.</summary>
            int Events { get; }

            void Start(double now);

            void Tick(double now);
        }

        // ---- rain: the real RainDirector --------------------------------------------------------------------------

        private sealed class RainRunner : IRunner, IRainPort<Handle>
        {
            private readonly World w;
            private readonly RainSettings s;
            private readonly RainDirector<Handle> director;
            private double t0;

            public RainRunner(World w, HazardArgs merged)
            {
                this.w = w;
                Args = merged;
                s = RainSettings.From(merged);
                director = new RainDirector<Handle>(w.Region, s, this);
            }

            public HazardArgs Args { get; }

            public HazardOutcome? Outcome { get; private set; }

            public int Events => director.Launched;

            public void Start(double now)
            {
                t0 = now;
                w.Now = 0.0;
                director.Start(0.0);
            }

            public void Tick(double now)
            {
                w.Now = now - t0;

                if (Outcome == null)
                    director.Tick(w.Now);
            }

            public Random Rng => w.Rng;

            public IReadOnlyList<Vector3> PlayerPositions() => w.PlayerPositions();

            public int? LiveCount => w.C.LiveCount;

            public bool Alive(Handle handle) => w.Alive(handle);

            public float GroundZ(float x, float y) => HazardPressureHarness.GroundZ;

            public bool HasCell(Vector3 point) => true;

            public Handle SpawnMarker(Vector3 point, double seconds)
            {
                var h = w.NewHandle(point, w.Now + seconds);
                h.Threat = new StaticThreat
                {
                    Centre = new Vector2(point.X, point.Y),
                    Reach = (float)s.BurstRadius + w.PlayerRadius,
                    SpawnAt = t0 + w.Now,
                    Until = t0 + w.Now + seconds,
                };

                w.AddThreat(h.Threat);
                return h;
            }

            public Handle SpawnBall(Vector3 point) => w.NewHandle(point);

            public bool Fall(Handle ball, Vector3 end, double seconds)
            {
                if (!w.Alive(ball))
                    return false;

                ball.Pos = end;
                return true;
            }

            public Handle SpawnBurst(Vector3 point, double seconds) => w.NewHandle(point, w.Now + seconds);

            public int Strike(Handle source, Vector3 centre, float radius, double window)
                => source == null || source.Dead ? 0 : w.Strike(source.Id, null, centre, radius, window);

            public void Destroy(Handle handle) => w.Destroy(handle);

            public void Say(string line) { }

            public void Log(string line) { }

            public void End(HazardOutcome outcome, string reason) => Outcome = outcome;
        }

        // ---- wisps: the real WispDirector -------------------------------------------------------------------------

        private sealed class WispRunner : IRunner, IWispPort<Handle>
        {
            private readonly World w;
            private readonly WispSettings s;
            private readonly string pattern;
            private WispDirector<Handle> director;
            private double t0;

            public WispRunner(World w, HazardArgs merged, string pattern)
            {
                this.w = w;
                Args = merged;
                this.pattern = pattern;
                s = WispSettings.From(merged);
            }

            public HazardArgs Args { get; }

            public HazardOutcome? Outcome { get; private set; }

            public int Events => director?.Pillars ?? 0;

            private float EdgeInset => (float)Args.GetDouble(HazardDefaults.EdgeInsetKey, HazardDefaults.EdgeInset, HazardDefaults.MinEdgeInset, HazardDefaults.MaxEdgeInset);

            /// <summary>WispSquadMechanic.Start: the squad at WispFormations.SpawnPoints, the follower at the first spot when follow=1.</summary>
            public void Start(double now)
            {
                t0 = now;
                w.Now = 0.0;

                var spots = WispFormations.SpawnPoints(w.Region, s.Count, EdgeInset, w.Rng, _ => true);
                var wisps = spots.Select(p => w.NewHandle(PointFor(p.X, p.Y))).ToList();

                if (wisps.Count == 0)
                {
                    Outcome = HazardOutcome.Aborted;
                    return;
                }

                var follower = s.Follow && spots.Count > 0 ? w.NewHandle(PointFor(spots[0].X, spots[0].Y)) : null;

                director = new WispDirector<Handle>(w.Region, s, pattern, this, wisps, follower);
                director.Start();
            }

            public void Tick(double now)
            {
                w.Now = now - t0;

                if (Outcome == null)
                    director.Tick(w.Now);
            }

            public Random Rng => w.Rng;

            public bool Alive(Handle handle) => w.Alive(handle);

            public Vector3 PositionAt(Handle handle, double now) => handle.Pos;

            public Vector3 PointFor(float x, float y) => new Vector3(x, y, HazardPressureHarness.GroundZ + w.Region.HazardOffset);

            public bool HasCell(Vector3 point) => true;

            public void MoveLegs(IReadOnlyList<(Handle Handle, Vector3 End)> group, double seconds, HazardMoveOptions options)
            {
                foreach (var (h, end) in group)
                    h.Pos = end;
            }

            public int? LiveCount => w.C.LiveCount;

            public Handle SpawnWisp(Vector3 point) => w.NewHandle(point);

            public bool MoveLeg(Handle handle, Vector3 end, double seconds)
            {
                if (!w.Alive(handle))
                    return false;

                handle.Pos = end;
                return true;
            }

            /// <summary>WispSquadMechanic's port: the FIRST player in the region.</summary>
            public bool TryFollowTarget(out Vector3 position, out uint targetId)
            {
                var inRegion = w.PlayersInRegion();

                if (inRegion.Count == 0)
                {
                    position = default;
                    targetId = 0;
                    return false;
                }

                position = w.Point3(inRegion[0].Pos.X, inRegion[0].Pos.Y);
                targetId = inRegion[0].Guid;
                return true;
            }

            /// <summary>WispSquadMechanic's port: every player in the region (position, id), in region order (follow_target=random draws from these).</summary>
            public IReadOnlyList<(Vector3 Position, uint Id)> FollowCandidates()
                => w.PlayersInRegion().Select(a => (w.Point3(a.Pos.X, a.Pos.Y), a.Guid)).ToList();

            public double TickSeconds => Dt;

            public Handle SpawnPillar(Vector3 point, double seconds)
            {
                var h = w.NewHandle(point, w.Now + seconds);

                // a pillar is a telegraph too: a dodger that is still in it leaves
                w.AddThreat(new StaticThreat
                {
                    Centre = new Vector2(point.X, point.Y),
                    Reach = s.HitRadius + w.PlayerRadius,
                    SpawnAt = t0 + w.Now,
                    Until = t0 + w.Now + seconds,
                });

                return h;
            }

            public Handle SpawnDestMark(Vector3 point, double seconds)
            {
                var h = w.NewHandle(point, w.Now + seconds);

                // the mark stands until the burst; the pillar then burns for burst seconds at the same point
                h.Threat = new StaticThreat
                {
                    Centre = new Vector2(point.X, point.Y),
                    Reach = s.HitRadius + w.PlayerRadius,
                    SpawnAt = t0 + w.Now,
                    Until = t0 + w.Now + seconds + s.Burst,
                };

                w.AddThreat(h.Threat);
                return h;
            }

            /// <summary>An outdoor arena has no ceiling.</summary>
            public Handle SpawnCeilingBeam(Vector3 pillar, double seconds) => null;

            public double Now => w.Now;

            public bool HasCeiling => false;

            public void Schedule(double seconds, Action step) => w.Schedule(seconds, step);

            public void Destroy(Handle handle) => w.Destroy(handle);

            public Vector3 RestOf(Handle handle) => handle.Pos;

            /// <summary>WispSquadMechanic's port: hitGroup = volley, none for volley 0.</summary>
            public int Strike(Handle source, Vector3 centre, float radius, double window, int volley)
                => source == null || source.Dead ? 0 : w.Strike(source.Id, volley == 0 ? (int?)null : volley, centre, radius, window);

            public int NewHitGroup() => w.NewHitGroup();

            public void Say(string line) { }

            public void Log(string line) { }

            public void End(HazardOutcome outcome, string reason) => Outcome = outcome;
        }

        // ---- balls: APPROXIMATE geometric re-hosting of RollingBallMechanic's cadence mode -------------------------
        // Every copied expression below is pinned against RollingBallMechanic.cs by HazardPressureTests.BallCopy_* pins.

        private sealed class BallRunner : IRunner
        {
            private sealed class Ball
            {
                public int Id;
                public BallRoute Route;
                public bool Alive = true;
                public HazardPath Path;
                public HazardPath Tail;
                public LaneThreat Threat;
            }

            private sealed class Wave
            {
                public int No;
                public BallWaveClock Clock;
                public BallParams Params;
                public BallWave Plan;
                public double Lead;
                public readonly Dictionary<int, Ball> Balls = new Dictionary<int, Ball>();
            }

            private sealed class Planned
            {
                public int No;
                public BallParams Params;
                public BallWave Plan;
                public double Lead;
            }

            private readonly World w;
            private readonly string pattern;
            private readonly BallParams p;
            private readonly BallCadence cadence;
            private readonly Dictionary<int, Wave> waves = new Dictionary<int, Wave>();
            private double t0;
            private bool ended;

            public BallRunner(World w, HazardArgs merged, string pattern)
            {
                this.w = w;
                Args = merged;
                this.pattern = pattern;
                p = BallRules.Read(merged, pattern);

                if (!p.Cadence)
                    throw new NotSupportedException("the approximate ball model covers cadence mode only (wave_every); the World Event row always sets it");

                cadence = new BallCadence(p, pattern);
            }

            public HazardArgs Args { get; }

            public HazardOutcome? Outcome { get; private set; }

            public int Events { get; private set; }

            private void Finish(HazardOutcome outcome)
            {
                ended = true;
                Outcome = outcome;
            }

            public void Start(double now)
            {
                t0 = now;
                w.Now = 0.0;
                LaunchDue();
            }

            private Vector3 At(Vector2 v) => w.Point3(v.X, v.Y);

            /// <summary>Hit: ctx.HitPlayers(ball.Handle, centre, p.BallRadius, HitWindowSeconds), no hit group.</summary>
            private int Hit(Ball b, Vector3 centre) => b.Alive ? w.Strike(b.Id, null, centre, p.BallRadius, RollingBallMechanic.HitWindowSeconds) : 0;

            public void Tick(double now)
            {
                if (ended)
                    return;

                var t = now - t0;

                // HazardManager.TickRun: AdvanceMotion (leg ends) first, then the due steps (w.Now below), then Mechanic.Tick
                w.SetClock(t);

                foreach (var wave in waves.Values.OrderBy(x => x.No).ToList())
                {
                    foreach (var b in wave.Balls.Values)
                    {
                        if (b.Path == null || !b.Alive || t + 1e-9 < b.Path.EndTime || wave.Clock.StateOf(b.Id) != BallState.Launched)
                            continue;

                        var path = b.Path;
                        b.Path = null; // the end snap clears the handle's path

                        var ball = b;

                        // RollGroup's OnLegEnd
                        if (BallHits.LegEnded(wave.Clock, b.Id, path, ref b.Tail, p.HitLag, p.Reach * 0.5f, pt => Hit(ball, pt)))
                            Kill(b);
                    }
                }

                w.Now = t;

                if (!ended)
                    TickCadence(t);
            }

            private void Kill(Ball b)
            {
                b.Alive = false;

                if (b.Threat != null)
                    b.Threat.Removed = true;
            }

            /// <summary>TickCadence: launch due, sweep every open wave (SweepWave), close finished waves, retry a held wave.</summary>
            private void TickCadence(double t)
            {
                LaunchDue();

                if (ended)
                    return;

                var open = waves.Values.OrderBy(x => x.No).ToList();

                foreach (var wave in open)
                {
                    foreach (var id in wave.Clock.Hittable())
                    {
                        var b = wave.Balls[id];

                        if (BallHits.Tick(wave.Clock, id, b.Alive, b.Path, b.Tail, t, p.HitLag, p.Reach * 0.5f, pt => Hit(b, pt)))
                            Kill(b);
                    }
                }

                foreach (var wave in open)
                {
                    var result = cadence.Close(wave.No);

                    if (result == null)
                        continue;

                    waves.Remove(wave.No);

                    foreach (var b in wave.Balls.Values)
                        Kill(b);

                    var verdict = cadence.Verdict(wave.No, result.Value);

                    if (verdict.HasValue)
                    {
                        Finish(verdict.Value.Outcome);
                        return;
                    }
                }

                if (cadence.HeldWave != 0)
                    LaunchDue();

                if (!ended && cadence.Held)
                    LaunchDue();
            }

            /// <summary>LaunchDue: THE ONE BALL-BUDGET READ (BallBudget.For(ctx.LiveCount, p.MaxBalls)).</summary>
            private void LaunchDue()
            {
                cadence.Launch(w.Now,
                    PlanWave,
                    planned => planned.Plan.Routes.Count,
                    () => BallBudget.For(w.C.LiveCount, p.MaxBalls),
                    (n, planned) =>
                    {
                        OpenWave(planned);
                        return !ended;
                    });
            }

            /// <summary>TryPlanWave + PlanCadenceWave: the fan aims at a player in the region now; anything else is BallPatterns.Plan.</summary>
            private Planned PlanWave(int n)
            {
                var waveParams = BallRules.CapToLiveCount(BallRules.ForWave(p, n), w.C.LiveCount);
                double? fanDistance = null;
                BallWave plan;

                if (pattern == BallPatterns.Fan)
                {
                    var origin = BallPatterns.FanOrigin(w.Region, p);
                    var spots = w.PlayersInRegion().Select(a => a.Pos).ToList();
                    var pick = BallPatterns.PickTarget(spots, origin, p.Target, w.Rng);

                    if (pick < 0)
                    {
                        Finish(HazardOutcome.Completed);
                        return null;
                    }

                    // the harness's players face +y (the mechanic reads the target's rotation)
                    var aim = BallPatterns.FanAim(origin, spots[pick], Vector2.UnitY, w.Region.Center, p.Reach);
                    plan = BallPatterns.PlanFanAlong(w.Region, waveParams, aim.Angle);
                    fanDistance = Vector2.Distance(origin, spots[pick]);
                }
                else
                    plan = BallPatterns.Plan(w.Region, pattern, waveParams, w.Rng, n, cadence.Distinct ? cadence.BusyWalls : null);

                var waveLead = BallRules.LeadFor(w.Region, pattern, waveParams, fanDistance);

                if (plan.Routes.Count == 0)
                {
                    Finish(BallVerdicts.EmptyPlan(n).Outcome);
                    return null;
                }

                return new Planned { No = n, Params = waveParams, Plan = plan, Lead = waveLead };
            }

            /// <summary>OpenCadenceWave: cadence.Open, then one scheduled spawn per delay group.</summary>
            private void OpenWave(Planned planned)
            {
                var wave = new Wave
                {
                    No = planned.No,
                    Clock = cadence.Open(planned.No, planned.Plan.Routes.Count, planned.Plan.Gaps.Select(g => g.Side)),
                    Params = planned.Params,
                    Plan = planned.Plan,
                    Lead = planned.Lead,
                };

                waves[wave.No] = wave;

                foreach (var group in planned.Plan.Routes.GroupBy(r => Math.Round(r.Delay, 3)))
                {
                    var routes = group.ToList();
                    w.Schedule(group.Key, () => Spawn(wave, routes));
                }
            }

            private bool IsLive(Wave wave) => !ended && waves.TryGetValue(wave.No, out var live) && ReferenceEquals(live, wave);

            /// <summary>CadenceSpawn + SpawnRoutes (every start has a cell on the flat plane, so no nudge), then the roll after the lead.</summary>
            private void Spawn(Wave wave, List<BallRoute> routes)
            {
                if (!IsLive(wave))
                    return;

                var group = new List<Ball>();

                foreach (var route in routes)
                {
                    var b = new Ball { Id = w.NewHandle(At(route.From)).Id, Route = route };
                    wave.Clock.OnSpawned(b.Id);
                    Events++;
                    wave.Balls[b.Id] = b;
                    group.Add(b);

                    var leg = BallRules.LegSeconds(route, wave.Params.Speed);

                    b.Threat = new LaneThreat
                    {
                        From = route.From,
                        To = route.To,
                        RollAt = t0 + w.Now + wave.Lead,
                        LegSeconds = leg,
                        Reach = p.BallRadius + w.PlayerRadius,
                        SpawnAt = t0 + w.Now,
                        Until = t0 + w.Now + wave.Lead + leg + p.HitLag + Dt,
                    };

                    w.AddThreat(b.Threat);
                }

                w.Schedule(wave.Lead, () =>
                {
                    if (IsLive(wave))
                        Roll(wave, group);
                });
            }

            /// <summary>RollGroup: one straight leg each at the wave's speed (BallRules.LegSeconds), launched now.</summary>
            private void Roll(Wave wave, List<Ball> group)
            {
                foreach (var b in group)
                {
                    if (!b.Alive)
                    {
                        wave.Clock.OnDied(b.Id);
                        continue;
                    }

                    var seconds = BallRules.LegSeconds(b.Route, wave.Params.Speed);
                    b.Path = new HazardPath(new[] { new HazardLeg(At(b.Route.From), At(b.Route.To), w.Now, seconds) }, rollRadius: p.BallRadius);
                    wave.Clock.OnLaunched(b.Id);
                }
            }
        }

        // =============================================================================================================
        // tables
        // =============================================================================================================

        public static readonly PressureBehaviour[] AllBehaviours = { PressureBehaviour.Stationary, PressureBehaviour.Dodger, PressureBehaviour.DodgerDegraded, PressureBehaviour.Kiter };

        public static string Letter(PressureBehaviour b)
        {
            switch (b)
            {
                case PressureBehaviour.Stationary: return "S";
                case PressureBehaviour.Dodger: return "D";
                case PressureBehaviour.DodgerDegraded: return "D50";
                default: return "K";
            }
        }

        /// <summary>The row label of a config: the ball pattern, "pool" (per-run pick, rolling only), "event" (rolling and rain), or "-".</summary>
        public static string RowLabel(PressureConfig c)
        {
            if (c.Kind != Balls)
                return c.Kind == Wisps ? WispRules.Mixed : "-";

            if (c.EventLevel)
                return "event";

            return c.Pattern ?? "pool";
        }

        public static string CsvHeader => "kind,args,label,behaviour,players,live_count,crowd_radius,seconds,seeds,pct_per_player_min_mean,pct_per_player_min_sd,pct_per_player_min_se,max_player_pct_per_min_mean,p90_player_pct_per_min_mean,hits_per_player_min_mean,events_per_min_mean,runs_started,runs_aborted,rain_runs,approximate";

        public static string CsvRow(PressureSummary s)
        {
            var c = s.Config;
            var inv = CultureInfo.InvariantCulture;

            return string.Join(",",
                c.Kind,
                "\"" + (c.Args ?? DefaultArgs(c.Kind)) + "\"",
                RowLabel(c),
                Letter(c.Behaviour),
                c.Players.ToString(inv),
                c.LiveCount.ToString(inv),
                c.CrowdRadius.ToString("0.#", inv),
                c.Seconds.ToString("0.#", inv),
                s.Runs.Count.ToString(inv),
                s.MeanPct.ToString("0.00", inv),
                s.SdPct.ToString("0.00", inv),
                s.SePct.ToString("0.00", inv),
                s.MeanMaxPct.ToString("0.00", inv),
                s.MeanP90Pct.ToString("0.00", inv),
                s.MeanHits.ToString("0.000", inv),
                s.EventsPerMinute.ToString("0.0", inv),
                s.Runs.Sum(r => r.RunsStarted).ToString(inv),
                s.Runs.Sum(r => r.RunsAborted).ToString(inv),
                s.Runs.Sum(r => r.RainRuns).ToString(inv),
                c.Kind == Balls ? "yes" : "no");
        }

        /// <summary>
        /// Two blocks: the group mean (mean, sd and se over seeds) and the worst-off player (max player %/min averaged over
        /// seeds, and the p90 player), one column per behaviour, one row per (label, crowd radius, live count, N).
        /// </summary>
        public static string Table(string title, IReadOnlyList<PressureSummary> rows)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            var groups = rows.GroupBy(r => (Label: RowLabel(r.Config), r.Config.CrowdRadius, r.Config.LiveCount, r.Config.Players))
                .OrderBy(g => g.Key.Label, StringComparer.Ordinal).ThenBy(g => g.Key.CrowdRadius).ThenBy(g => g.Key.LiveCount).ThenBy(g => g.Key.Players)
                .ToList();

            string Head(string what) => string.Format(inv, "{0,-9} {1,5} {2,5} {3,3} {4,-4} | {5,-27} {6,-27} {7,-27} {8,-27} {9}",
                "label", "crowd", "count", "N", "seed", "S " + what, "D (perfect, upper bound)", "D50 (1.5 s, 50%)", "K (kiter)", "events/min");

            sb.AppendLine(title);
            sb.AppendLine("MEAN %maxHP lost per player-minute: mean sd se over seeds");
            sb.AppendLine(Head("(stationary)"));

            foreach (var g in groups)
            {
                string Cell(PressureBehaviour b)
                {
                    var s = g.FirstOrDefault(r => r.Config.Behaviour == b);
                    return s == null ? "" : string.Format(inv, "{0,6:0.0} sd{1,5:0.0} se{2,5:0.0}", s.MeanPct, s.SdPct, s.SePct);
                }

                sb.AppendLine(Line(g.Key, g.First().Runs.Count, Cell, g.First().EventsPerMinute));
            }

            sb.AppendLine("WORST PLAYER %maxHP per minute (mean over seeds) / p90 player");
            sb.AppendLine(Head("(stationary)"));

            foreach (var g in groups)
            {
                string Cell(PressureBehaviour b)
                {
                    var s = g.FirstOrDefault(r => r.Config.Behaviour == b);
                    return s == null ? "" : string.Format(inv, "max{0,7:0.0} p90{1,7:0.0}", s.MeanMaxPct, s.MeanP90Pct);
                }

                sb.AppendLine(Line(g.Key, g.First().Runs.Count, Cell, g.First().EventsPerMinute));
            }

            return sb.ToString();

            string Line((string Label, float CrowdRadius, int LiveCount, int Players) k, int seeds, Func<PressureBehaviour, string> cell, double events)
                => string.Format(inv, "{0,-9} {1,5:0} {2,5} {3,3} {4,-4} | {5,-27} {6,-27} {7,-27} {8,-27} {9:0.0}",
                    k.Label, k.CrowdRadius, k.LiveCount, k.Players, seeds,
                    cell(PressureBehaviour.Stationary), cell(PressureBehaviour.Dodger), cell(PressureBehaviour.DodgerDegraded), cell(PressureBehaviour.Kiter), events);
        }
    }
}
