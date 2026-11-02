using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The "balls" hazard: the Rolling Death patterns (ring-out, ring-in, row, random, cross, fan). Thin shell: every
    /// decision (routes, gaps, no-escape bounds, the fair lead, the wave lifecycle) lives in the pure
    /// <see cref="BallPatterns"/>, <see cref="BallRules"/> and <see cref="BallWaveClock"/>, so it is tested without
    /// a world.
    ///
    /// WORKS ANYWHERE. Geometry comes only from ctx.Region (a rect, a circle, an ad-hoc region around the caller, an
    /// outdoor terrain region), never from a landblock, coordinate or z literal. A ring's or fan's centre defaults to
    /// the region centre; args cx/cy move it (landblock-local metres, inside the region), and the /hazard command's
    /// at=me fills them from the caller. A future boss or world event starts a run with
    /// <c>HazardManager.Start(region, "balls", HazardArgs.Parse("pattern=row dir=n gap=7 waves=3"), host, seed)</c>.
    ///
    /// ONE LIFECYCLE FOR EVERY PATTERN. A wave plans its routes (BallPatterns.Plan), spawns each ball at its start
    /// (ctx.Spawn at ctx.PointFor, hovering ball_z above the ground; a start with no cell is nudged along its lane,
    /// invariant 10), waits the wave's lead (BallRules.LeadFor) with pillars up, then rolls every ball with MoveLegs
    /// (Follow = Ground, GroundOffset = ball_z; balls that share a leg time share one schedule). A rolling ball hits
    /// through ctx.HitPlayers at every point of the stretch it covered since the last tick (BallSweep, steps of half a
    /// reach, so a fast ball on a slow tick cannot step over a player), once per ball per player, only while the ball
    /// is in flight (or in its lagged tail) and its object is alive.
    ///
    /// LATENCY COMPENSATION (args hit_lag, seconds, default 0.15 in code, clamped 0..0.5; BallHits). A client starts
    /// flying a leg only when the velocity packet arrives and the server knows each player's position only as of their
    /// last report, so the server's ball leads the one a player SEES by about a round trip. Every point of the path at
    /// path time t is therefore tested against player positions no earlier than server time t + hit_lag: each sweep ends
    /// at min(now - hit_lag, leg end). A ball whose leg ENDED keeps its object, at rest on its end point, and sweeps its
    /// last hit_lag seconds (its tail, BallState.Tail) on the following ticks; it is destroyed in the tick its tail is
    /// done. A ball that DIED before its leg ended never hits again. hit_lag=0 is the old behaviour exactly: the leg end
    /// sweeps to the end and the ball is destroyed in that tick. THE WAVE ENDS WHEN EVERY BALL OF IT HAS ENDED ITS LEG
    /// AND ITS TAIL, OR DIED (BallWaveClock.WaveOver); a timeout fallback of the ideal time plus max(1 s, 3 ticks) plus
    /// hit_lag and one more tick (when hit_lag &gt; 0) writes off a ball that vanished silently. The next wave starts wave_gap later.
    ///
    /// OCCUPANCY (args occupancy, default 0.75 in code, clamped 0.25..2; random pattern only). The fraction of the narrowest
    /// side that the expected balls in flight may block; BallRules.Validate refuses a launch rate over it. It gates Validate
    /// only: no ball behaves differently at run time.
    ///
    /// DEAD HANDLES (one policy for every pattern). A ball whose object dies mid-run (/reload-landblock) is counted
    /// as lost, logged once and dropped before any hit is tested; the wave carries on without it. The run ends
    /// Completed when the last wave finishes, whether or not balls were lost - EXCEPT that a wave in which EVERY
    /// ball was PROVEN lost (failed to spawn or died; a timeout write-off is not proof) ends the run Aborted with a
    /// one-line reason, as the wisps do when no wisp is left. A pattern that plans no routes at all is Aborted too.
    /// The verdicts are the pure <see cref="BallVerdicts"/>.
    ///
    /// CADENCE MODE (args wave_every, opt-in). The waves launch at the fixed run times of BallRules.LaunchTimes, and a
    /// wave never waits on the previous wave's balls, so several waves roll at once. Each launched wave keeps its own
    /// BallWaveClock (the pure <see cref="BallCadence"/> owns them); everything else (planning, the nudge, the lead, the
    /// hits, the dead-handle policy, the timeout fallback) is the same code as above. wave_gap is not used. Without
    /// wave_every the run is exactly the sequential lifecycle above. max_in_flight (opt-in) caps the waves in flight: a
    /// wave whose time has come is held while the cap of earlier waves is still open, and launches in the tick one of
    /// them finishes; a wave whose balls have all ended their legs frees its slot then, while its lagged tails are still
    /// swept (the cap counts what a player sees rolling; BallCadence.InFlight), and the ball budget likewise does not
    /// count tails. max_balls (opt-in, cadence only), or the run's live count which replaces it, budgets BALLS: a due
    /// wave launches when nothing is in flight or in-flight + its planned size fits, else it is held WITH its plan
    /// (BallCadence.Launch) and retried each tick. Every hit passes ctx.DamageType (args damage_type), text unset.
    /// distinct_dirs (opt-in, row and cross only) also holds a wave while no wall is free of the waves in
    /// flight, and otherwise draws its wall(s) from the free ones (BallCadence.Due, BallPatterns.Plan's busy walls).
    /// </summary>
    public sealed class RollingBallMechanic : IHazardMechanic
    {
        /// <summary>The hit window every pattern uses: +infinity, i.e. one hit per ball per player.</summary>
        public const double HitWindowSeconds = double.PositiveInfinity;

        private sealed class Ball
        {
            public HazardHandle Handle;
            public BallRoute Route;

            /// <summary>The finished path, kept at the leg end for the lagged tail sweep (the handle's Path is cleared by the end snap). Null before.</summary>
            public HazardPath Tail;
        }

        private BallParams p;

        /// <summary>The CURRENT wave's numbers: <see cref="p"/> with speed, lead and wave_gap ramped for this wave (BallRules.ForWave). Set by StartWave; every callback of a wave is guarded by its wave number, so it is always the live wave's.</summary>
        private BallParams wp;
        private BallWaveClock clock;
        private double lead;
        private double waveStart;
        private bool waveActive;
        private float ballZ;
        private int liveBalls;
        private bool scaleChecked;
        private string phase = "starting";
        private readonly Dictionary<int, Ball> balls = new Dictionary<int, Ball>();

        public string Kind => HazardMechanicRegistry.Balls;

        public IReadOnlyList<string> Patterns => BallPatterns.All;

        public IEnumerable<uint> RequiredWcids(HazardArgs args) => HazardBallWcids.Required(args, BallWcid(args), HazardDefaults.EffectHostWcid);

        private static uint BallWcid(HazardArgs args) => (uint)args.GetInt("ball_wcid", (int)HazardDefaults.RollingDeathRedWcid, 1, int.MaxValue);

        public string Validate(HazardRegion region, HazardArgs args, string pattern)
            => BallRules.Validate(region, args, pattern) ?? HazardContext.BadDamageType(args) ?? HazardBallWcids.Validate(args);

        /// <summary>This run's ball colours (ball_wcids, else ball_wcid) and their own picker (HazardBallWcids), made at the first spawn.</summary>
        private IReadOnlyList<uint> ballWcids;
        private HazardBallColourPicker wcidPicker;

        private uint NextBallWcid(HazardContext ctx)
        {
            if (ballWcids == null)
            {
                ballWcids = HazardBallWcids.Candidates(ctx.Args, BallWcid(ctx.Args));
                wcidPicker = HazardBallWcids.Picker(ctx.Run.Seed);
            }

            return HazardBallWcids.Pick(ballWcids, wcidPicker);
        }

        public void Start(HazardContext ctx)
        {
            p = BallRules.Read(ctx.Args, ctx.Pattern);
            wp = p;
            clock = new BallWaveClock(p.Waves);
            ballZ = (float)ctx.Args.GetDouble(HazardDefaults.BallHoverKey, HazardDefaults.BallHover, HazardDefaults.MinBallHover, HazardDefaults.MaxBallHover);

            ctx.Log(FormattableString.Invariant(
                $"balls start pattern={ctx.Pattern} speed={p.Speed:0.##}->{p.SpeedEnd ?? p.Speed:0.##} lead>={p.Lead:0.##}->{p.LeadEnd ?? p.Lead:0.##} escape_speed={p.EscapeSpeed:0.##} gap={p.Gap:0.##} count={p.Count} waves={p.Waves} wave_gap={p.WaveGap:0.##}->{p.WaveGapEnd ?? p.WaveGap:0.##} wave_every={(p.Cadence ? FormattableString.Invariant($"{p.WaveEvery:0.##}->{p.WaveEveryEnd ?? p.WaveEvery:0.##}") : "off")} stream={p.Stream:0.##} ball_z={ballZ:0.##} radius={p.BallRadius:0.##}+{p.PlayerRadius:0.##} hit_lag={p.HitLag:0.##}"));

            if (p.Cadence)
            {
                StartCadence(ctx);
                return;
            }

            StartWave(ctx);
        }

        /// <summary>
        /// Plans one wave's routes (the fan aims at a player in the region at THIS moment). False when the run was ended
        /// instead: a fan with nobody to aim at completes the run.
        /// </summary>
        private bool TryPlanWave(HazardContext ctx, BallParams waveParams, int waveNo, out BallWave plan, out double? fanDistance, out string aimedAt, IReadOnlyCollection<BallSide> busyWalls = null)
        {
            fanDistance = null;
            aimedAt = null;

            if (ctx.Pattern == BallPatterns.Fan)
            {
                // The aim is fixed NOW, at wave start, from where the target stands; the balls launch `lead` seconds
                // later and never home. The lead is the sidestep time for this target's real distance.
                var inRegion = ctx.PlayersInRegion();
                var origin = BallPatterns.FanOrigin(ctx.Region, p);
                var spots = inRegion.Select(t => new Vector2(t.Position.X, t.Position.Y)).ToList();
                var pick = BallPatterns.PickTarget(spots, origin, p.Target, ctx.Rng);

                if (pick < 0)
                {
                    plan = null;
                    ctx.Say($"[hazard #{ctx.Run.Id}] The Rolling Death finds no one in the region.");
                    ctx.End(HazardOutcome.Completed, "no player in the region");
                    return false;
                }

                // A target standing on the origin (at=me target=nearest) gives no direction: FanAim then follows the
                // target's facing (Position's forward is Rotation applied to +y).
                var rot = inRegion[pick].Player.Location?.Rotation ?? Quaternion.Identity;
                var forward = Vector3.Transform(Vector3.UnitY, rot);
                var facing = new Vector2(forward.X, forward.Y);

                var aim = BallPatterns.FanAim(origin, spots[pick], facing, ctx.Region.Center, p.Reach);

                plan = BallPatterns.PlanFanAlong(ctx.Region, waveParams, aim.Angle);
                fanDistance = Vector2.Distance(origin, spots[pick]);
                aimedAt = inRegion[pick].Player.Name + aim.Note;

                if (aim.Degenerate)
                    ctx.Log(FormattableString.Invariant($"fan target {inRegion[pick].Player.Name} is {fanDistance:0.00} m from the origin (under one reach {p.Reach:0.##} m); aim from {aim.Source} = {aim.Angle * 180.0 / Math.PI:0.#} degrees (facing ({facing.X:0.00}, {facing.Y:0.00}))"));
            }
            else
            {
                // the wave number only matters to ring-alt (odd waves out, even waves in); the busy walls only to
                // distinct_dirs (a cadence row or cross draws its walls from the ones no wave in flight is using)
                plan = BallPatterns.Plan(ctx.Region, ctx.Pattern, waveParams, ctx.Rng, waveNo, busyWalls);
            }

            return true;
        }

        private void StartWave(HazardContext ctx)
        {
            // this wave's speed, lead and wave_gap (ramped across the waves; today's numbers when no _end key is given)
            wp = BallRules.CapToLiveCount(BallRules.ForWave(p, clock.Wave + 1), ctx.LiveCount);

            if (!TryPlanWave(ctx, wp, clock.Wave + 1, out var plan, out var fanDistance, out var aimedAt))
                return;

            lead = BallRules.LeadFor(ctx.Region, ctx.Pattern, wp, fanDistance);

            // Nothing to roll: end before announcing anything.
            if (plan.Routes.Count == 0)
            {
                ctx.Log($"wave {clock.Wave + 1} planned no routes; ending");
                var empty = BallVerdicts.EmptyPlan(clock.Wave + 1);

                ctx.Say($"[hazard #{ctx.Run.Id}] The Rolling Death has nowhere to roll in this region.");
                ctx.End(empty.Outcome, empty.Reason);
                return;
            }

            var waveNo = clock.StartWave(plan.Routes.Count);
            balls.Clear();
            waveActive = true;
            waveStart = ctx.Now;
            phase = $"wave {waveNo}/{p.Waves} lead-in";

            var note = p.Marker && plan.Markers.Count > 0 && plan.MarkerNote.Length > 0 ? "; " + plan.MarkerNote : "";

            ctx.Say(FormattableString.Invariant(
                $"[hazard #{ctx.Run.Id}] Rolling Death, wave {waveNo}/{p.Waves}: {plan.Summary}{(aimedAt != null ? ", aimed at " + aimedAt : "")}{BallRules.MoveClause(lead, note)}"));

            if (p.Marker)
            {
                foreach (var m in plan.Markers)
                    ctx.SpawnTimedEffect(new Vector3(m.X, m.Y, ctx.GroundZ(m.X, m.Y)), p.MarkerScript, MemoryLayout.FloorOrientation(p.MarkerScript), BallRules.MarkerSeconds(lead), 1f, "marker-gap", grounded: true);
            }

            // Routes that appear together are spawned together; only the random pattern has more than one delay.
            foreach (var group in plan.Routes.GroupBy(r => Math.Round(r.Delay, 3)))
            {
                var routes = group.ToList();
                ctx.Schedule(group.Key, () => SpawnGroup(ctx, routes, waveNo));
            }

            ctx.Schedule(BallRules.FallbackSeconds(plan, wp.Speed, lead, clock.TickSeconds, p.HitLag), () => Fallback(ctx, waveNo, plan, lead));
        }

        private void SpawnGroup(HazardContext ctx, List<BallRoute> routes, int waveNo)
        {
            if (waveNo != clock.Wave || !waveActive)
                return;

            var group = SpawnRoutes(ctx, routes, waveNo, clock, balls);

            ctx.Schedule(lead, () => Launch(ctx, group, waveNo));
        }

        /// <summary>
        /// Spawns <paramref name="routes"/> at their starts for wave <paramref name="waveNo"/>, recording each in
        /// <paramref name="waveClock"/> and <paramref name="waveBalls"/> (the sequential run's own, or one cadence wave's).
        /// Returns the ids that spawned.
        /// </summary>
        private List<int> SpawnRoutes(HazardContext ctx, List<BallRoute> routes, int waveNo, BallWaveClock waveClock, Dictionary<int, Ball> waveBalls)
        {
            var group = new List<int>(routes.Count);

            foreach (var planned in routes)
            {
                var route = planned;

                // INVARIANT 10: a moving ball is never raised out of a solid, so a start with no cell is nudged along
                // its lane (at most one reach) until one holds it.
                var start = BallPatterns.NudgeStart(route.From, route.To,
                    pt => ctx.HasCell(ctx.PointFor(pt.X, pt.Y, HazardFollow.Ground, ballZ)), p.Reach);

                if (start == null)
                {
                    waveClock.OnSpawnFailed();
                    ctx.Log(FormattableString.Invariant($"wave {waveNo}: no cell at ({route.From.X:0.0}, {route.From.Y:0.0}) or within {p.Reach:0.0} m along its lane; ball dropped{HoleNote(route)}"));
                    continue;
                }

                if (start.Value != route.From)
                {
                    ctx.Log(FormattableString.Invariant($"wave {waveNo}: start ({route.From.X:0.0}, {route.From.Y:0.0}) has no cell; nudged to ({start.Value.X:0.0}, {start.Value.Y:0.0})"));
                    route = new BallRoute(start.Value, route.To, route.Delay, route.Tag, route.Speed);
                }

                var handle = ctx.Spawn(new HazardSpawnSpec
                {
                    Wcid = NextBallWcid(ctx),
                    Position = ctx.PointFor(route.From.X, route.From.Y, HazardFollow.Ground, ballZ),
                    Scale = BallRules.SpawnScale(p),
                    Moving = true,
                    Tag = "ball",
                    HitRadius = p.BallRadius,
                });

                if (handle == null)
                {
                    waveClock.OnSpawnFailed();
                    ctx.Log($"wave {waveNo}: spawn refused for a ball at ({route.From.X:0.0}, {route.From.Y:0.0}){HoleNote(route)}"); // Spawn logged why
                    continue;
                }

                waveClock.OnSpawned(handle.Id);
                waveBalls[handle.Id] = new Ball { Handle = handle, Route = route };
                group.Add(handle.Id);

                CheckScale(ctx, handle);
            }

            return group;
        }

        /// <summary>A row or cross wall missing a ball has a hole, which is a safe spot: the wave still fires, but the log says so.</summary>
        private static string HoleNote(BallRoute r) => r.Tag.StartsWith("row-", StringComparison.Ordinal) ? "; the wall now has a hole (an unmarked safe spot)" : "";

        /// <summary>The geometry assumes the weenie's real scale gives the radius it was told; say so once if not.</summary>
        private void CheckScale(HazardContext ctx, HazardHandle handle)
        {
            if (scaleChecked || p.BallRadiusGiven || handle.Object == null)
                return;

            scaleChecked = true;

            var actual = HazardDefaults.RollingDeathRadiusAtScale1 * (handle.Object.ObjScale ?? 1f);

            if (Math.Abs(actual - p.BallRadius) > 0.05f)
                ctx.Log(FormattableString.Invariant($"WARNING ball radius assumed {p.BallRadius:0.##} m but the spawned ball is {actual:0.##} m (scale {handle.Object.ObjScale}); pass ball_radius={actual:0.##}"));
        }

        private void Launch(HazardContext ctx, List<int> group, int waveNo)
        {
            if (waveNo != clock.Wave || !waveActive)
                return;

            phase = $"wave {waveNo}/{p.Waves} rolling";

            RollGroup(ctx, group, clock, balls, wp);
        }

        /// <summary>
        /// Rolls the spawned <paramref name="group"/> of one wave (<paramref name="waveClock"/> and <paramref name="waveBalls"/>
        /// are that wave's) at <paramref name="waveParams"/>' speed. A ball that died during its lead is dropped.
        /// </summary>
        private void RollGroup(HazardContext ctx, List<int> group, BallWaveClock waveClock, Dictionary<int, Ball> waveBalls, BallParams waveParams)
        {
            var options = new HazardMoveOptions
            {
                Follow = HazardFollow.Ground,
                GroundOffset = ballZ,
                RollRadius = p.BallRadius,
                OnLegEnd = (h, path) =>
                {
                    // the finished path is still on the handle here. hit_lag 0: sweep the last stretch before the end snap
                    // and destroy. hit_lag > 0: keep the path and the object; Tick sweeps the tail at its lagged times
                    if (waveBalls.TryGetValue(h.Id, out var b))
                    {
                        if (!BallHits.LegEnded(waveClock, h.Id, path, ref b.Tail, p.HitLag, p.Reach * 0.5f, pt => Hit(ctx, b, pt)))
                            return;
                    }
                    else
                    {
                        waveClock.OnLegEnd(h.Id);
                    }

                    ctx.Schedule(0.0, () => ctx.Destroy(h));
                },
            };

            var live = new List<Ball>();

            foreach (var id in group)
            {
                if (waveBalls.TryGetValue(id, out var ball) && ball.Handle.Alive)
                    live.Add(ball);
                else
                    waveClock.OnDied(id);
            }

            // Balls with the same leg time move on one shared schedule (invariant 2); a rect row, or a circle's
            // equal-length lanes, is one group.
            foreach (var legGroup in live.GroupBy(b => Math.Round(BallRules.LegSeconds(b.Route, waveParams.Speed), 2)))
            {
                var members = legGroup.Select(b => (b.Handle, ctx.PointFor(b.Route.To.X, b.Route.To.Y, HazardFollow.Ground, ballZ))).ToList();

                ctx.MoveLegs(members, BallRules.LegSeconds(legGroup.First().Route, waveParams.Speed), options);

                foreach (var b in legGroup)
                    waveClock.OnLaunched(b.Handle.Id);
            }
        }

        /// <summary>
        /// THE ONE HIT for every pattern (shared invariant 3), at one swept centre: through ctx.HitPlayers, radius = ball
        /// radius (the player's radius is added inside), once per ball per player (<see cref="HitWindowSeconds"/>). The
        /// test is 2D on purpose: nobody jumps over or ducks under a ball. WHICH centres, and when (the contiguous sweep in
        /// steps of half a reach, lagged by hit_lag), is the pure <see cref="BallHits"/>.
        /// </summary>
        private int Hit(HazardContext ctx, Ball ball, Vector3 centre)
            => ctx.HitPlayers(ball.Handle, centre, p.BallRadius, HitWindowSeconds, damageType: ctx.DamageType);

        public void Tick(HazardContext ctx)
        {
            clock.ObserveNow(ctx.Now);

            if (cadence != null)
            {
                TickCadence(ctx);
                return;
            }

            // 1. dead handles first, so a destroyed ball never hits (HazardContext.HitPlayers also refuses it)
            foreach (var id in clock.Unresolved())
            {
                if (balls.TryGetValue(id, out var gone) && !gone.Handle.Alive)
                {
                    clock.OnDied(id);
                    ctx.Log($"ball {id} died before its leg ended; carrying on without it");
                }
            }

            // 2. hits, for balls in flight or in their lagged tail only (clock.Hittable())
            SweepWave(ctx, clock, balls);

            liveBalls = balls.Values.Count(b => b.Handle.Alive);

            // 3. the wave ends on its balls
            if (waveActive)
                FinishIfOver(ctx);
        }

        /// <summary>
        /// Sweeps every hittable ball of one wave (<paramref name="clock"/>, the sequential run's own or one cadence wave's;
        /// <paramref name="waveBalls"/>) at server time now (BallHits.Tick: a rolling ball to now - hit_lag, a ball in its
        /// tail likewise) and destroys each ball whose tail is done. Dead handles must already have been dropped (step 1 of
        /// Tick and TickCadence).
        /// </summary>
        private void SweepWave(HazardContext ctx, BallWaveClock clock, Dictionary<int, Ball> waveBalls)
        {
            foreach (var id in clock.Hittable())
            {
                var b = waveBalls[id];
                var h = b.Handle;

                if (BallHits.Tick(clock, id, h.Alive, h.Path, b.Tail, ctx.Now, p.HitLag, p.Reach * 0.5f, pt => Hit(ctx, b, pt)))
                    ctx.Destroy(h);
            }
        }

        private void FinishIfOver(HazardContext ctx)
        {
            // The snapshot is taken BEFORE anything can start the next wave (StartWave resets the clock), and the
            // run's fate comes from it alone (BallVerdicts).
            var result = clock.Close();

            if (result.Step == BallWaveStep.Wait)
                return;

            waveActive = false;

            foreach (var b in balls.Values)
                ctx.Destroy(b.Handle);

            balls.Clear();

            var verdict = BallVerdicts.WaveVerdict(result);

            if (verdict.HasValue)
            {
                phase = verdict.Value.Outcome == HazardOutcome.Completed ? "done" : "aborted";
                ctx.Say(verdict.Value.Outcome == HazardOutcome.Completed
                    ? $"[hazard #{ctx.Run.Id}] The Rolling Death has passed."
                    : $"[hazard #{ctx.Run.Id}] The Rolling Death has been lost.");
                ctx.End(verdict.Value.Outcome, verdict.Value.Reason);
                return;
            }

            phase = $"wave {clock.Wave}/{p.Waves} resting";
            ctx.Schedule(wp.WaveGap, () => StartWave(ctx));
        }

        /// <summary>
        /// The timeout fallback: only for a ball that vanished without a leg end or a death we noticed. It waits for
        /// ideal arrival + max(1 s, 3 ticks), measured from the wave start, and only then writes the rest off.
        /// </summary>
        private void Fallback(HazardContext ctx, int waveNo, BallWave plan, double waveLead)
        {
            if (waveNo != clock.Wave || !waveActive)
                return;

            var deadline = waveStart + BallRules.FallbackSeconds(plan, wp.Speed, waveLead, clock.TickSeconds, p.HitLag);

            if (ctx.Now + 1e-6 < deadline)
            {
                ctx.Schedule(deadline - ctx.Now, () => Fallback(ctx, waveNo, plan, waveLead));
                return;
            }

            var written = clock.ForceResolve();
            ctx.Log($"wave {waveNo} fallback: {written} ball(s) never reported and were written off");
            FinishIfOver(ctx);
        }

        // ================================================================ cadence mode (wave_every)

        /// <summary>One launched wave of a cadence run: its own clock (owned by <see cref="cadence"/>), balls and numbers.</summary>
        private sealed class CadenceWave
        {
            public int No;
            public BallWaveClock Clock;
            public BallParams Params;
            public BallWave Plan;
            public double Lead;
            public double Start;
            public readonly Dictionary<int, Ball> Balls = new Dictionary<int, Ball>();
        }

        private BallCadence cadence;
        private double runStart;
        private bool cadenceEnded;
        private int cadenceSpawned;
        private int cadenceLost;
        private int heldNoted;
        private readonly Dictionary<int, CadenceWave> waves = new Dictionary<int, CadenceWave>();

        private void StartCadence(HazardContext ctx)
        {
            cadence = new BallCadence(p, ctx.Pattern);
            runStart = ctx.Now;

            var times = string.Join(", ", cadence.LaunchTimes.Select(t => t.ToString("0.##", CultureInfo.InvariantCulture)));

            ctx.Log(p.MaxInFlight.HasValue
                ? FormattableString.Invariant($"balls cadence: launches at {times} s at the earliest; max_in_flight {cadence.CapFor(1)}->{cadence.CapFor(p.Waves)} holds a wave while that many are in flight, and the schedule then slides (each wave is due its period after the previous one launched); wave_gap unused")
                : FormattableString.Invariant($"balls cadence: launches at {times} s; waves overlap, wave_gap unused"));

            LaunchDue(ctx);
        }

        /// <summary>Launches every wave whose time has come; with max_in_flight, only while the cap leaves room (BallCadence.Due).</summary>
        private void LaunchDue(HazardContext ctx)
        {
            // THE ONE BALL-BUDGET READ: when a wave is due (and each tick a held wave is retried). No live count and no
            // max_balls: the budget is null and BallCadence.Launch is Due, plan, open, wave by wave, as it always was.
            cadence.Launch(ctx.Now - runStart,
                n => PlanCadenceWave(ctx, n),
                planned => planned.Plan.Routes.Count,
                () => BallBudget.For(ctx.LiveCount, p.MaxBalls),
                (n, planned) =>
                {
                    OpenCadenceWave(ctx, planned);
                    return !cadenceEnded;
                });

            if (cadence.HeldWave != 0 && cadence.HeldWave != budgetNoted)
            {
                budgetNoted = cadence.HeldWave;
                ctx.Log($"wave {cadence.HeldWave}/{p.Waves} held by the ball budget: {cadence.HeldFor.InFlight} ball(s) in flight + {cadence.HeldFor.Size} planned > {cadence.HeldFor.Budget}");
            }
        }

        private int budgetNoted;

        /// <summary>A planned cadence wave: its numbers, plan, lead and fan aim. Held whole by the ball budget, so a held wave is never re-planned.</summary>
        private sealed class PlannedWave
        {
            public int No;
            public BallParams Params;
            public BallWave Plan;
            public double Lead;
            public string AimedAt;
        }

        /// <summary>Plans wave <paramref name="n"/> (the random numbers are drawn here, in wave order). Null when the run ended instead: a fan with nobody to aim at, or a plan with no routes.</summary>
        private PlannedWave PlanCadenceWave(HazardContext ctx, int n)
        {
            // under a live count the wave is planned no bigger than the budget (count-parameterised patterns only)
            var waveParams = BallRules.CapToLiveCount(BallRules.ForWave(p, n), ctx.LiveCount);

            if (!TryPlanWave(ctx, waveParams, n, out var plan, out var fanDistance, out var aimedAt, cadence.Distinct ? cadence.BusyWalls : null))
            {
                cadenceEnded = true;
                return null;
            }

            var waveLead = BallRules.LeadFor(ctx.Region, ctx.Pattern, waveParams, fanDistance);

            if (plan.Routes.Count == 0)
            {
                ctx.Log($"wave {n} planned no routes; ending");
                var empty = BallVerdicts.EmptyPlan(n);

                cadenceEnded = true;
                ctx.Say($"[hazard #{ctx.Run.Id}] The Rolling Death has nowhere to roll in this region.");
                ctx.End(empty.Outcome, empty.Reason);
                return null;
            }

            return new PlannedWave { No = n, Params = waveParams, Plan = plan, Lead = waveLead, AimedAt = aimedAt };
        }

        private void OpenCadenceWave(HazardContext ctx, PlannedWave planned)
        {
            var n = planned.No;
            var waveParams = planned.Params;
            var plan = planned.Plan;
            var waveLead = planned.Lead;
            var aimedAt = planned.AimedAt;

            // distinct_dirs: the wave's walls are busy from here (spawn and lead included) until the wave finishes
            var w = new CadenceWave { No = n, Clock = cadence.Open(n, plan.Routes.Count, plan.Gaps.Select(g => g.Side)), Params = waveParams, Plan = plan, Lead = waveLead, Start = ctx.Now };
            waves[n] = w;
            phase = $"wave {n}/{p.Waves} launched, {waves.Count} in flight";

            var note = p.Marker && plan.Markers.Count > 0 && plan.MarkerNote.Length > 0 ? "; " + plan.MarkerNote : "";

            ctx.Say(FormattableString.Invariant(
                $"[hazard #{ctx.Run.Id}] Rolling Death, wave {n}/{p.Waves}: {plan.Summary}{(aimedAt != null ? ", aimed at " + aimedAt : "")}{BallRules.MoveClause(waveLead, note)}"));

            if (p.Marker)
            {
                foreach (var m in plan.Markers)
                    ctx.SpawnTimedEffect(new Vector3(m.X, m.Y, ctx.GroundZ(m.X, m.Y)), p.MarkerScript, MemoryLayout.FloorOrientation(p.MarkerScript), BallRules.MarkerSeconds(waveLead), 1f, "marker-gap", grounded: true);
            }

            foreach (var group in plan.Routes.GroupBy(r => Math.Round(r.Delay, 3)))
            {
                var routes = group.ToList();
                ctx.Schedule(group.Key, () => CadenceSpawn(ctx, w, routes));
            }

            ctx.Schedule(BallRules.FallbackSeconds(plan, waveParams.Speed, waveLead, clock.TickSeconds, p.HitLag), () => CadenceFallback(ctx, w));
        }

        private bool IsLive(CadenceWave w) => !cadenceEnded && waves.TryGetValue(w.No, out var live) && ReferenceEquals(live, w);

        private void CadenceSpawn(HazardContext ctx, CadenceWave w, List<BallRoute> routes)
        {
            if (!IsLive(w))
                return;

            var group = SpawnRoutes(ctx, routes, w.No, w.Clock, w.Balls);

            ctx.Schedule(w.Lead, () =>
            {
                if (IsLive(w))
                    RollGroup(ctx, group, w.Clock, w.Balls, w.Params);
            });
        }

        private void TickCadence(HazardContext ctx)
        {
            if (cadenceEnded)
                return;

            LaunchDue(ctx);

            if (cadenceEnded)
                return;

            var open = waves.Values.OrderBy(w => w.No).ToList();

            // 1. dead handles first, in every open wave, so a destroyed ball never hits
            foreach (var w in open)
            {
                foreach (var id in w.Clock.Unresolved())
                {
                    if (w.Balls.TryGetValue(id, out var gone) && !gone.Handle.Alive)
                    {
                        w.Clock.OnDied(id);
                        ctx.Log($"ball {id} (wave {w.No}) died before its leg ended; carrying on without it");
                    }
                }
            }

            // 2. hits, for balls in flight or in their lagged tail only, across every overlapping wave
            foreach (var w in open)
                SweepWave(ctx, w.Clock, w.Balls);

            liveBalls = open.Sum(w => w.Balls.Values.Count(b => b.Handle.Alive));

            // Describe runs on the command thread: it reads these plain fields, never the dictionaries
            cadenceSpawned = cadence.Spawned;
            cadenceLost = cadence.Lost;

            // 3. each wave ends on its own balls
            foreach (var w in open)
            {
                FinishCadenceWave(ctx, w);

                if (cadenceEnded)
                    return;
            }

            // 4. a wave held by max_in_flight launches in the tick a slot frees, not one tick later
            // a wave held by the ball budget launches in the tick balls free, not one tick later
            if (cadence.HeldWave != 0)
            {
                LaunchDue(ctx);

                if (cadenceEnded)
                    return;
            }

            if (cadence.Held)
            {
                LaunchDue(ctx);

                if (cadence.Held && heldNoted != cadence.Launched + 1)
                {
                    heldNoted = cadence.Launched + 1;
                    ctx.Log(cadence.HeldForWalls
                        ? $"wave {heldNoted}/{p.Waves} held: {cadence.InFlight} wave(s) in flight, no free wall (distinct_dirs; busy {string.Join(",", cadence.BusyWalls.Select(BallPatterns.Name))})"
                        : $"wave {heldNoted}/{p.Waves} held: {cadence.InFlight} wave(s) in flight, cap {cadence.CapFor(heldNoted)}");
                }
            }
        }

        private void FinishCadenceWave(HazardContext ctx, CadenceWave w)
        {
            if (!IsLive(w))
                return;

            var result = cadence.Close(w.No);

            if (result == null)
                return;

            waves.Remove(w.No);

            foreach (var b in w.Balls.Values)
                ctx.Destroy(b.Handle);

            var verdict = cadence.Verdict(w.No, result.Value);

            if (!verdict.HasValue)
            {
                phase = cadence.AllLaunched ? $"waves done, {waves.Count} in flight" : $"wave {cadence.Launched}/{p.Waves} launched, {waves.Count} in flight";
                return;
            }

            cadenceEnded = true;

            foreach (var other in waves.Values)
                foreach (var b in other.Balls.Values)
                    ctx.Destroy(b.Handle);

            waves.Clear();

            phase = verdict.Value.Outcome == HazardOutcome.Completed ? "done" : "aborted";
            ctx.Say(verdict.Value.Outcome == HazardOutcome.Completed
                ? $"[hazard #{ctx.Run.Id}] The Rolling Death has passed."
                : $"[hazard #{ctx.Run.Id}] The Rolling Death has been lost.");
            ctx.End(verdict.Value.Outcome, verdict.Value.Reason);
        }

        /// <summary>The sequential mode's timeout fallback, for one cadence wave: ideal arrival + settle after ITS launch.</summary>
        private void CadenceFallback(HazardContext ctx, CadenceWave w)
        {
            if (!IsLive(w))
                return;

            var deadline = w.Start + BallRules.FallbackSeconds(w.Plan, w.Params.Speed, w.Lead, clock.TickSeconds, p.HitLag);

            if (ctx.Now + 1e-6 < deadline)
            {
                ctx.Schedule(deadline - ctx.Now, () => CadenceFallback(ctx, w));
                return;
            }

            var written = w.Clock.ForceResolve();
            ctx.Log($"wave {w.No} fallback: {written} ball(s) never reported and were written off");
            FinishCadenceWave(ctx, w);
        }

        public void OnStop(HazardContext ctx, HazardOutcome outcome)
        {
            // a cadence run stopped from outside (HazardManager.Stop) is inert from here on: no later call launches a wave
            cadenceEnded = true;

            ctx.Log(cadence != null
                ? $"balls stop outcome={outcome} waves={cadence.Launched}/{cadence.Waves} (cadence) spawned={cadence.Spawned} lost={cadence.Lost}"
                : $"balls stop outcome={outcome} waves={clock?.Wave ?? 0} spawned={clock?.Spawned ?? 0} lost={clock?.Lost ?? 0}");
        }

        public string Describe(HazardContext ctx)
            => cadence != null
                ? string.Format(CultureInfo.InvariantCulture, "{0}; {1} ball(s) live, {2} spawned, {3} lost", phase, liveBalls, cadenceSpawned, cadenceLost)
                : string.Format(CultureInfo.InvariantCulture, "{0}; {1} ball(s) live, {2} spawned, {3} lost", phase, liveBalls, clock?.Spawned ?? 0, clock?.Lost ?? 0);

        public string Reveal(HazardContext ctx) => null;
    }
}
