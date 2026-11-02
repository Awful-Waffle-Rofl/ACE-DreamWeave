using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// Everything the rain director needs from the world, so it can be driven by a recording fake in tests and by a
    /// <see cref="HazardContext"/> in the game (see RainMechanic). <typeparamref name="THandle"/> is HazardHandle live.
    /// </summary>
    public interface IRainPort<THandle> where THandle : class
    {
        Random Rng { get; }

        /// <summary>Live players in the region, in the region's frame. Called only when a drop is aimed.</summary>
        IReadOnlyList<Vector3> PlayerPositions();

        /// <summary>The run's live count (HazardContext.LiveCount); null = the args. The director reads it ONLY at a launch decision.</summary>
        int? LiveCount { get; }

        bool Alive(THandle handle);

        /// <summary>Ground z at (x, y).</summary>
        float GroundZ(float x, float y);

        /// <summary>Is there a cell at this point for a moving body (no raise)?</summary>
        bool HasCell(Vector3 point);

        /// <summary>The ground marker (a harmless effect host) at <paramref name="point"/>, standing <paramref name="seconds"/>; null when it would not spawn.</summary>
        THandle SpawnMarker(Vector3 point, double seconds);

        /// <summary>The falling ball, a moving body, at <paramref name="point"/> (drop height); null when it would not spawn.</summary>
        THandle SpawnBall(Vector3 point);

        /// <summary>One straight leg for the ball to <paramref name="end"/> over <paramref name="seconds"/>, not auto-stopped (the director destroys it at impact). False for a dead handle.</summary>
        bool Fall(THandle ball, Vector3 end, double seconds);

        /// <summary>The burst effect at <paramref name="point"/>, standing <paramref name="seconds"/>; null when it would not spawn.</summary>
        THandle SpawnBurst(Vector3 point, double seconds);

        /// <summary>The one damage call: players within radius of centre, once per window, ctx.DamageType, text unset. Returns how many were hit.</summary>
        int Strike(THandle source, Vector3 centre, float radius, double window);

        /// <summary>Removes one spawned object now. Safe on a dead or null handle.</summary>
        void Destroy(THandle handle);

        void Say(string line);

        void Log(string line);

        void End(HazardOutcome outcome, string reason);
    }

    /// <summary>
    /// The rain run, with the world behind <see cref="IRainPort{THandle}"/>. FIRE AT WILL: launch decisions come at random
    /// gaps averaging (lead + fall) / cap, so about cap balls are under way and they are never in step. At a decision the
    /// director reads the cap (LiveCount ?? max_falling, 1..64 - the ONLY read of the live count) and launches one drop
    /// when fewer than cap are under way. A drop is: a ground marker at a placeable point standing for the whole fall, and a ball spawned straight above it at drop height, which after
    /// its lead falls in one straight leg at <c>speed</c> to rest on the ground. At the end of the leg: ONE strike at the
    /// impact point with burst_radius, a burst effect, and the ball is destroyed. The run stops launching after
    /// <c>duration</c>, waits for the balls in the air, and ends Completed.
    ///
    /// FAIR WARNING. Every drop's lead is raised so that lead + its own fall is at least
    /// 0.5 + (burst_radius + player_radius) / escape_speed (<see cref="RainRules.LeadFor"/>), measured at the drop's own ground.
    /// AIMING. With probability target_share a drop is aimed: it picks a live player at random and lands at a uniform point in the
    /// disc of radius target_spread round them (up to 8 tries at the same acceptance as HazardRegion.TrySamplePoint: placeable and
    /// edge_inset inside); an unaimed drop, an empty region, or 8 rejections fall back to a uniform point in the region.
    /// A drop stays inside one landblock (it falls straight down at one point).
    /// </summary>
    public sealed class RainDirector<THandle> where THandle : class
    {
        private sealed class Drop
        {
            public THandle Ball;
            public THandle Marker;
            public Vector3 Impact;
            public Vector3 End;
            public double Fall;
            public double MoveAt;
            public double SpawnAt;
            public Vector3 Start;
            public double ImpactAt = double.NaN;
            public bool Falling;
        }

        private readonly HazardRegion region;
        private readonly RainSettings s;
        private readonly IRainPort<THandle> port;
        private readonly List<Drop> drops = new List<Drop>();
        private readonly double nominalLifetime;

        private double runStart;
        private double nextDecisionAt;
        private bool started;
        private bool ended;
        private bool leadNoted;
        private bool noGroundNoted;

        private int launched;
        private int aimed;
        private int impacts;
        private int hits;
        private int lost;
        private int failures;
        private int consecutiveFailures;
        private int lastCap;

        public RainDirector(HazardRegion region, RainSettings settings, IRainPort<THandle> port)
        {
            this.region = region;
            s = settings;
            this.port = port;
            nominalLifetime = RainRules.NominalLifetime(region, settings);
        }

        public int Launched => launched;

        public int Impacts => impacts;

        /// <summary>Launched drops that landed in a player's disc (not the uniform fallback).</summary>
        public int Aimed => aimed;

        public int Hits => hits;

        public int Lost => lost;

        /// <summary>Balls under way (spawned, not yet burst).</summary>
        public int InFlight => drops.Count;

        public bool Ended => ended;

        /// <summary>The cap read at the latest launch decision (0 before the first).</summary>
        public int LastCap => lastCap;

        public void Start(double now)
        {
            started = true;
            runStart = now;
            nextDecisionAt = now;
        }

        public void Tick(double now)
        {
            if (!started || ended)
                return;

            ResolveDrops(now);

            if (ended)
                return;

            var launching = now < runStart + s.Duration;

            if (launching && now >= nextDecisionAt)
                Decide(now);

            if (ended)
                return;

            if (!launching)
            {
                var giveUp = runStart + s.Duration + nominalLifetime * RainRules.DrainLifetimes + RainRules.DrainGraceSeconds;

                if (drops.Count == 0 || now >= giveUp)
                {
                    ended = true;
                    port.Log(FormattableString.Invariant($"rain done launched={launched} aimed={aimed} impacts={impacts} hits={hits} lost={lost} failures={failures}"));
                    port.End(HazardOutcome.Completed, "rain finished");
                }
            }
        }

        /// <summary>Starts the fall of every ball whose lead is over, and bursts every ball whose leg is over.</summary>
        private void ResolveDrops(double now)
        {
            for (var i = 0; i < drops.Count; i++)
            {
                var d = drops[i];

                // a drop whose marker was put up early (the fair-warning top-up): the ball appears when its time comes
                if (d.Ball == null)
                {
                    if (now < d.SpawnAt)
                        continue;

                    d.Ball = port.SpawnBall(d.Start);

                    if (d.Ball == null)
                    {
                        lost++;
                        port.Log($"rain ball could not be spawned at ({d.Impact.X:0.0}, {d.Impact.Y:0.0}); its marker is removed");
                        port.Destroy(d.Marker);
                        drops.RemoveAt(i--);
                        continue;
                    }

                    d.MoveAt = now + s.Lead;
                }

                if (!port.Alive(d.Ball))
                {
                    // /reload-landblock destroyed it: no hit, no burst, and its marker would be a false telegraph
                    lost++;
                    port.Log($"rain ball lost before impact at ({d.Impact.X:0.0}, {d.Impact.Y:0.0}); carrying on");
                    port.Destroy(d.Marker);
                    drops.RemoveAt(i--);
                    continue;
                }

                if (!d.Falling)
                {
                    if (now < d.MoveAt)
                        continue;

                    if (!port.Fall(d.Ball, d.End, d.Fall))
                    {
                        lost++;
                        port.Destroy(d.Ball);
                        port.Destroy(d.Marker);
                        drops.RemoveAt(i--);
                        continue;
                    }

                    d.Falling = true;
                    d.ImpactAt = now + d.Fall;
                    continue;
                }

                if (now + 1e-9 < d.ImpactAt)
                    continue;

                Impact(d);
                drops.RemoveAt(i--);
            }
        }

        private void Impact(Drop d)
        {
            impacts++;
            hits += port.Strike(d.Ball, d.Impact, (float)s.BurstRadius, double.PositiveInfinity);

            if (port.SpawnBurst(d.Impact, RainRules.BurstSeconds) == null)
                port.Log($"rain burst effect could not be spawned at ({d.Impact.X:0.0}, {d.Impact.Y:0.0}); the damage was dealt anyway");

            port.Destroy(d.Ball);
            port.Destroy(d.Marker);
        }

        /// <summary>One launch decision: read the cap, draw the next gap, and launch one drop when fewer than cap are under way.</summary>
        private void Decide(double now)
        {
            var cap = RainRules.CapFor(port.LiveCount, s); // the ONLY read of the live count
            lastCap = cap;

            nextDecisionAt = now + RainRules.NextGap(port.Rng, RainRules.MeanGap(nominalLifetime, cap));

            if (drops.Count >= cap)
                return;

            if (TryLaunch(now))
            {
                consecutiveFailures = 0;
                return;
            }

            failures++;
            consecutiveFailures++;

            if (launched == 0 && consecutiveFailures >= RainRules.GiveUpAfterFailures)
            {
                ended = true;
                port.Say("The rain could not be placed: no open spot at drop height in this region.");
                port.End(HazardOutcome.Aborted, "no rain drop could be placed");
            }
        }

        /// <summary>
        /// The impact point: aimed at a random player's disc with probability target_share, else (or when no player, or 8
        /// rejections) uniform over the region. target_share 0 draws nothing extra, so its stream is the old uniform one.
        /// </summary>
        private bool TryPickPoint(out Vector2 p, out bool wasAimed)
        {
            wasAimed = false;
            p = default;

            if (s.TargetShare > 0.0)
            {
                var aim = s.TargetShare >= 1.0 || port.Rng.NextDouble() < s.TargetShare;

                if (aim)
                {
                    var players = port.PlayerPositions();

                    if (players != null && players.Count > 0)
                    {
                        var target = players[port.Rng.Next(players.Count)];

                        for (var i = 0; i < RainRules.AimAttempts; i++)
                        {
                            var radius = s.TargetSpread * Math.Sqrt(port.Rng.NextDouble());
                            var angle = 2.0 * Math.PI * port.Rng.NextDouble();
                            var x = target.X + (float)(radius * Math.Cos(angle));
                            var y = target.Y + (float)(radius * Math.Sin(angle));

                            if (region.IsPlaceable(x, y) && region.DistanceToEdge(x, y) >= s.EdgeInset)
                            {
                                p = new Vector2(x, y);
                                wasAimed = true;
                                return true;
                            }
                        }
                    }
                }
            }

            return region.TrySamplePoint(port.Rng, out p, 16, s.EdgeInset);
        }

        private bool TryLaunch(double now)
        {
            if (!TryPickPoint(out var p, out var wasAimed))
                return false;

            var ground = port.GroundZ(p.X, p.Y);
            var dropZ = s.DropZ ?? ground + RainRules.DefaultDropHeight;
            var endZ = ground + RainRules.BallRadius;

            if (!float.IsFinite(ground) || dropZ - endZ < RainRules.MinFallMetres)
            {
                if (!noGroundNoted)
                {
                    noGroundNoted = true;
                    port.Log(FormattableString.Invariant($"rain: drop_z {dropZ:0.##} is not above the ground {ground:0.##} at ({p.X:0.0}, {p.Y:0.0}); that drop is skipped (noted once)"));
                }

                return false;
            }

            var start = new Vector3(p.X, p.Y, (float)dropZ);

            if (!port.HasCell(start))
                return false;

            var fall = RainRules.FallSeconds(dropZ, ground, s.Speed);
            var lead = RainRules.LeadFor(s, fall);
            var early = lead - s.Lead; // > 0 only when the fair-warning floor needs more than the asked lead

            if (lead > s.Lead + 1e-9 && !leadNoted)
            {
                leadNoted = true;
                port.Log(FormattableString.Invariant($"rain: lead raised from {s.Lead:0.###} to {lead:0.###} s for a {fall:0.###} s fall (fair-warning floor {RainRules.WarningFloor(s):0.###} s): the marker goes up {early:0.###} s before the ball, which never hovers longer than lead; noted once"));
            }

            var impact = new Vector3(p.X, p.Y, ground);

            // the marker first: a ball with no marker would be an unannounced hit
            var marker = port.SpawnMarker(impact, early + s.Lead + fall + 0.5);

            if (marker == null)
                return false;

            // the ball hangs for lead only; any top-up for the floor is spent on the marker standing before it
            THandle ball = null;

            if (early <= 1e-9)
            {
                ball = port.SpawnBall(start);

                if (ball == null)
                {
                    port.Destroy(marker);
                    return false;
                }
            }

            launched++;

            if (wasAimed)
                aimed++;

            drops.Add(new Drop
            {
                Ball = ball,
                Marker = marker,
                Impact = impact,
                End = new Vector3(p.X, p.Y, (float)endZ),
                Fall = fall,
                MoveAt = now + s.Lead,
                SpawnAt = now + Math.Max(0.0, early),
                Start = start,
            });

            return true;
        }

        public string Describe()
            => FormattableString.Invariant($"rain, {drops.Count} falling (cap {lastCap}), launched {launched}, aimed {aimed}, impacts {impacts}, hits {hits}");
    }
}
