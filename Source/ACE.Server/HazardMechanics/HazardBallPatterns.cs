using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>A compass side of a region. North is +y, East is +x (HazardMoveGate.Heading's convention).</summary>
    public enum BallSide
    {
        North,
        East,
        South,
        West,
    }

    /// <summary>One ball's straight 2D path. z is never here: the mechanic derives it from the region (the hover rule).</summary>
    public readonly struct BallRoute
    {
        public BallRoute(Vector2 from, Vector2 to, double delay, string tag, float speed = 0f)
        {
            From = from;
            To = to;
            Delay = delay;
            Tag = tag;
            Speed = speed;
        }

        /// <summary>This ball's own speed in m/s (a stream ramps it per launch), or 0 to use the wave's <see cref="BallParams.Speed"/>.</summary>
        public float Speed { get; }

        public Vector2 From { get; }

        public Vector2 To { get; }

        /// <summary>Seconds after the wave starts that this ball appears (0 for every pattern but random). It moves <c>lead</c> seconds later.</summary>
        public double Delay { get; }

        public string Tag { get; }

        public float Length => Vector2.Distance(From, To);
    }

    /// <summary>Where a fan's aim came from (<see cref="BallPatterns.FanAim"/>): the target itself, or a fallback for a target standing on the origin.</summary>
    public enum FanAimSource
    {
        Target,
        Facing,
        RegionCentre,
        PlusX,
    }

    /// <summary>A fan's aim angle (radians) and its <see cref="FanAimSource"/>.</summary>
    public readonly struct FanAimResult
    {
        public FanAimResult(double angle, FanAimSource source)
        {
            Angle = angle;
            Source = source;
        }

        public double Angle { get; }

        public FanAimSource Source { get; }

        /// <summary>True when the target was within one reach of the origin and a fallback picked the aim.</summary>
        public bool Degenerate => Source != FanAimSource.Target;

        /// <summary>The announcement's note for a fallback aim; empty for an aim at the target.</summary>
        public string Note
        {
            get
            {
                switch (Source)
                {
                    case FanAimSource.Facing: return " (standing on the origin: fired along their facing)";
                    case FanAimSource.RegionCentre: return " (standing on the origin: fired towards the region centre)";
                    case FanAimSource.PlusX: return " (standing on the origin: fired east)";
                    default: return "";
                }
            }
        }
    }
    /// <summary>One wave: the balls' routes plus what the tests and the telegraph need to know about the safe spots.</summary>
    public sealed class BallWave
    {
        public List<BallRoute> Routes { get; } = new List<BallRoute>();

        /// <summary>Ground points to mark with a pillar during the lead: each row's gap centre on its start line, a cross's intersection.</summary>
        public List<Vector2> Markers { get; } = new List<Vector2>();

        /// <summary>Each row's side and gap centre along its lateral axis (x for North/South, y for East/West).</summary>
        public List<(BallSide Side, float Lateral)> Gaps { get; } = new List<(BallSide, float)>();

        /// <summary>A cross's guaranteed safe spot: where the two gap lanes intersect. Null for every other pattern.</summary>
        public Vector2? SafePoint { get; set; }

        public string Summary { get; set; } = "";

        /// <summary>What the pillars mean, for the announcement ("a pillar marks the way through", "pillars mark the line of fire"). Empty when there are none.</summary>
        public string MarkerNote { get; set; } = "";
    }

    /// <summary>
    /// The numbers one balls run uses, read through HazardArgs with a documented default and clamp (shared invariant
    /// 5). Pure: <see cref="BallRules.Read"/> builds it from args alone, so Validate (no context yet) and the
    /// mechanic read the very same values. A bad value never throws: it sets <see cref="Problem"/> and Validate refuses.
    /// </summary>
    public sealed class BallParams
    {
        /// <summary>Roll and hit radius: args "ball_radius", default 1.80 (0.80 x scale 2.25), clamped 0.1..6.</summary>
        public float BallRadius { get; init; } = HazardDefaults.RollingDeathRadius;

        /// <summary>True when "ball_radius" was given: the spawned ball is then scaled to it (<see cref="BallRules.SpawnScale"/>), so the model matches the hits.</summary>
        public bool BallRadiusGiven { get; init; }

        /// <summary>args "escape_speed", m/s, default 3, clamped 1..12: how fast a player is assumed to flee. Sizes every fair-lead and fan-lead floor.</summary>
        public double EscapeSpeed { get; init; } = HazardDefaults.EscapeSpeed;

        /// <summary>args "player_radius" (the framework's own key and default).</summary>
        public float PlayerRadius { get; init; } = (float)HazardDefaults.PlayerRadius;

        /// <summary>
        /// args "hit_lag", seconds, default <see cref="BallRules.DefaultHitLag"/> (0.15; the default lives HERE, in code, not
        /// in the hazard_mech_balls string), clamped 0..<see cref="BallRules.MaxHitLag"/>: LATENCY COMPENSATION. A client
        /// starts flying a leg only when its velocity packet arrives, and the server knows a player's position only as of
        /// their last report, so the server's ball leads the ball a player SEES by about a round trip. Every point of a
        /// ball's path at path time t is therefore hit-tested against player positions no earlier than server time t +
        /// hit_lag (<see cref="BallHits"/>): each sweep ends at min(now - hit_lag, leg end), and a ball whose leg ENDED keeps
        /// sweeping its last hit_lag seconds (its tail, <see cref="BallState.Tail"/>) at rest on its end point before it is
        /// destroyed. 0 is exactly the old behaviour: no tail, the leg end sweeps to the end and destroys the ball.
        /// </summary>
        public double HitLag { get; init; } = BallRules.DefaultHitLag;

        /// <summary>
        /// args "occupancy", default <see cref="HazardDefaults.BallsRandomOccupancy"/> (0.75; the default lives in code, not in
        /// the hazard_mech_balls string), clamped <see cref="HazardDefaults.BallsMinOccupancy"/>..<see cref="HazardDefaults.BallsMaxOccupancy"/>:
        /// RANDOM PATTERN ONLY, the fraction of the narrowest side that the balls in flight may block (<see cref="BallRules.Validate"/>).
        /// Gates Validate only; it changes no ball. A run whose balls come from all sides may raise it.
        /// </summary>
        public double Occupancy { get; init; } = HazardDefaults.BallsRandomOccupancy;

        /// <summary>args "edge_inset" (the framework's own key and default 2 m).</summary>
        public float Inset { get; init; } = (float)HazardDefaults.EdgeInset;

        /// <summary>args "speed", m/s, default 3, clamped 0.5..12.</summary>
        public float Speed { get; init; } = (float)HazardDefaults.BallsSpeed;

        /// <summary>args "lead", seconds, default 2, clamped 0.5..30 (0..30 with fair_lead=0). The EFFECTIVE lead is never below the fair-lead floor (<see cref="BallRules.EffectiveLead"/>) unless fair_lead=0.</summary>
        public double Lead { get; init; } = HazardDefaults.BallsLead;

        /// <summary>
        /// args "fair_lead", default true: the lead is raised to the fair-lead floor (<see cref="BallRules.FairLead"/>, and a
        /// fan's <see cref="BallRules.FanLead"/> at its target's distance) and never under 0.5 s. false (opt-in, fair_lead=0): no
        /// floor at all, "lead" is used as written and may be 0, so the balls move the moment they appear. Validate then
        /// refuses only what is geometrically impossible, never a lead that is too short to be fair.
        /// </summary>
        public bool FairLeadFloor { get; init; } = true;

        /// <summary>args "gap", metres between the flank balls' centres (row, cross), default 6, clamped 1..60.</summary>
        public float Gap { get; init; } = (float)HazardDefaults.BallsGap;

        /// <summary>
        /// args "gaps", openings per wall (row, cross), default 1, clamped 1..6. 1 is the classic row: one opening,
        /// placed exactly as before. N &gt; 1 opens N openings, each <see cref="Gap"/> wide, at random non-overlapping
        /// positions with at least one ball-width (2 x reach) of wall between neighbours and at both ends
        /// (<see cref="BallPatterns.GapCentres"/>). Ignored by every other pattern.
        /// </summary>
        public int Gaps { get; init; } = 1;

        /// <summary>
        /// args "no_overlap", default false (opt-in; row, cross): every wall is laid at a pitch of 2 x reach +
        /// <see cref="HazardDefaults.BallsNoOverlapMargin"/>, so no two balls' reach discs overlap. Every stretch of wall
        /// (a wall end to an opening, an opening to the next) is a whole number of pitches, at least one, and the span
        /// left over (under one pitch) widens the openings, split at random (<see cref="BallPatterns.NoOverlapWall"/>). Off, every
        /// wall is laid exactly as before. Ignored by every other pattern.
        /// </summary>
        public bool NoOverlap { get; init; }

        /// <summary>
        /// args "origin_clear", metres, default 0 (off), clamped 0..20 (fan, ring-out, ring-in, ring-alt): a fan's and a
        /// ring-out's balls start this far out from the origin along their rays, and a ring-in's balls stop this far
        /// short of it, so nothing spawns inside (or rolls into) whatever stands on the origin. 0 plans exactly as before.
        /// Ignored by every other pattern.
        /// </summary>
        public float OriginClear { get; init; }

        /// <summary>
        /// args "distinct_dirs", default false (opt-in; row, cross, cadence with max_in_flight): a new wave's wall (a row)
        /// or walls (a cross: one north/south, one east/west) are drawn uniformly from the walls no wave still in flight
        /// is using, and a wave whose time has come is HELD while no wall is free (<see cref="BallCadence.Due"/>). Off,
        /// nothing is drawn or held differently.
        /// </summary>
        public bool DistinctDirs { get; init; }

        /// <summary>args "count": ring balls (default 8) or random launches per wave (default 6), clamped 1..64.</summary>
        public int Count { get; internal set; }

        /// <summary>args "waves", default 4, clamped 1..50.</summary>
        public int Waves { get; init; } = HazardDefaults.BallsWaves;

        /// <summary>args "wave_gap", seconds between a wave's end and the next wave, default 3, clamped 0..120.</summary>
        public double WaveGap { get; init; } = HazardDefaults.BallsWaveGap;

        /// <summary>args "window", seconds one random wave's launches are spread over, default 20, clamped 2..300.</summary>
        public double Window { get; init; } = HazardDefaults.BallsWindow;

        /// <summary>args "marker", default true: a pillar at each gap centre / the cross intersection during the lead.</summary>
        public bool Marker { get; init; } = true;

        /// <summary>args "marker_script" (decimal or 0x hex), default <see cref="HazardDefaults.PillarScript"/>: the effect script of every marker pillar (gap, cross, ray and fan-line markers alike).</summary>
        public uint MarkerScript { get; init; } = HazardDefaults.PillarScript;

        /// <summary>
        /// args "ray_markers", default false (opt-in, review C11): ring-out and ring-in put a pillar every
        /// BallsFanMarkerSpacing m along each ray during the lead. Off, a ring plans exactly as before (no markers).
        /// </summary>
        public bool RayMarkers { get; init; }

        /// <summary>args "dir": n, s, e, w or random (default). The side a row starts from.</summary>
        public string Dir { get; init; } = "random";

        /// <summary>args "dirs": two adjacent sides such as "ne", or random (default). A cross's two start sides.</summary>
        public string Dirs { get; init; } = "random";

        /// <summary>args "cx"/"cy": the ring centre in landblock-local metres. Null: the region centre.</summary>
        public Vector2? Centre { get; init; }

        /// <summary>args "spread", degrees across the whole fan, default 30, clamped 0..120 (fan only).</summary>
        public float Spread { get; init; } = 30f;

        /// <summary>args "target": random (default) or nearest (fan only).</summary>
        public string Target { get; init; } = "random";

        /// <summary>
        /// args "speed_end", m/s, clamped like "speed": the last wave's speed (random with a stream: the last launch's).
        /// Null when not given: no ramp, every wave runs at <see cref="Speed"/>. See <see cref="BallRules.ForWave"/>.
        /// </summary>
        public float? SpeedEnd { get; init; }

        /// <summary>args "lead_end", seconds, clamped like "lead": the last wave's lead (still raised to the fair-lead floor unless fair_lead=0). Null: no ramp.</summary>
        public double? LeadEnd { get; init; }

        /// <summary>args "wave_gap_end", seconds, clamped like "wave_gap": the last USED gap (the one before the last wave; the gap after the last wave is never used). Null: no ramp.</summary>
        public double? WaveGapEnd { get; init; }

        /// <summary>
        /// args "wave_every", seconds, clamped 0.5..120, OPT-IN (null when not given): the CADENCE mode. Wave k+1 launches
        /// this long after wave k launched, whether or not wave k's balls have finished, so waves overlap and
        /// "wave_gap" is not used. Off (the default), a wave starts wave_gap after the previous wave's balls are all
        /// accounted for, exactly as before. Ramped over the Waves - 1 launch-to-launch periods like wave_gap
        /// (<see cref="BallRules.ForWave"/>: wave k's value is the period from wave k's launch to wave k+1's).
        /// </summary>
        public double? WaveEvery { get; init; }

        /// <summary>args "wave_every_end", seconds, clamped like "wave_every": the last period (wave N-1 to wave N). Null: no ramp. Ignored without wave_every.</summary>
        public double? WaveEveryEnd { get; init; }

        /// <summary>
        /// args "max_in_flight", whole waves, clamped 1..50, OPT-IN and cadence only (null when not given): a cadence wave
        /// launches when its time comes AND fewer than this many earlier waves are still in flight (launched and not yet
        /// finished); otherwise it launches the moment the count drops below. Ramped over the run's waves like "speed"
        /// (wave 1 the start value, the last wave <see cref="MaxInFlightEnd"/>), rounded to a whole number
        /// (<see cref="BallRules.ForWave"/>). Without it a cadence wave never waits.
        /// </summary>
        public int? MaxInFlight { get; init; }

        /// <summary>args "max_in_flight_end", clamped like "max_in_flight": the last wave's cap. Null: no ramp. Ignored without max_in_flight.</summary>
        public double? MaxInFlightEnd { get; init; }

        /// <summary>
        /// args "max_balls", whole balls, clamped <see cref="BallBudget.Min"/>..<see cref="BallBudget.Max"/>, OPT-IN and cadence
        /// only (null when not given): a due cadence wave launches when nothing is in flight or the balls in flight plus
        /// the wave's planned size are at most this (the run's live count replaces it when set). A held wave keeps its plan.
        /// </summary>
        public int? MaxBalls { get; init; }

        /// <summary>True when "wave_every" was given: the waves launch on a fixed cadence and overlap (see <see cref="WaveEvery"/>).</summary>
        public bool Cadence => WaveEvery.HasValue;

        internal BallParams CloneParams() => (BallParams)MemberwiseClone();

        /// <summary>
        /// args "stream", seconds, default 0 (off), clamped 0..300; random pattern only. When above 0 the run is ONE wave
        /// whose <see cref="Count"/> launches are spread over <see cref="Window"/> = stream, and "speed" to "speed_end"
        /// ramps launch by launch instead of wave by wave.
        /// </summary>
        public double Stream { get; init; }

        /// <summary>The first thing wrong with the raw args (an unknown dir, half a centre), or null.</summary>
        public string Problem { get; init; }

        /// <summary>Ball radius plus player radius: the 2D distance at which a ball hits.</summary>
        public float Reach => BallRadius + PlayerRadius;
    }

    /// <summary>
    /// PURE rules for the balls mechanic (shared invariant 6): reading args, the no-escape bounds Validate enforces,
    /// the fair-lead floor and the wave length. No world, no PropertyManager, no clock.
    ///
    /// THE BOUNDS (reach h = ball radius + player radius; a ball hits a player whose 2D distance to its centre is
    /// at most h; defaults h = 1.80 + 0.50 = 2.30 m):
    ///
    ///   MinGap W = 2 h + 0.5 m. A player standing midway between two balls whose centres are G apart is G/2 from
    ///   each, clear iff G/2 &gt; h. W leaves 0.25 m of margin per side.
    ///
    ///   ROW / CROSS. gap G &gt;= W, and it must FIT: G + 2 h &lt;= S, where S is the side's lateral span (the region's
    ///   width or height between the lateral insets). The 2 h is one ball-width of wall, so a row is still a row.
    ///   Every side the run could pick is checked (dir=random checks all four).
    ///
    ///   RING-OUT / RING-IN with N balls. Adjacent rays are 2 pi / N apart, so the chord between them at radius r is
    ///   2 r sin(pi / N). Balls are densest at the centre (chord 0), so a player must find the first lane wide enough.
    ///   Rule: at r_esc = 0.6 x R_min the chord must be at least W, where R_min = DistanceToEdge(centre) - edge_inset
    ///   is the shortest possible ray (a rect's nearest wall, a circle's radius minus the offset of the centre).
    ///   Equivalently N &lt;= pi / asin(W / (2 r_esc)). The lane radius r_ref = W / (2 sin(pi / N)) is where the first
    ///   safe lane opens; the fair-lead floor makes a player at the centre able to WALK to it.
    ///
    ///   RANDOM. Concurrent balls m = (count / window) x (L_max / speed), L_max the region's longest extent. Each
    ///   blocks a lane 2 h wide of the narrower axis. Rule: m x 2 h &lt;= occupancy x S_min (the narrowest side's span; args "occupancy", default 0.75, clamped 0.25..2).
    ///   Launch times are stratified (one per window/count slot), but m is an AVERAGE-CASE estimate of the balls in
    ///   flight (L_max / speed over-states a typical lane's travel time, and a stratified draw can still bunch two
    ///   neighbouring slots), not a worst-case bound.
    ///
    ///   FAIR LEAD. A player fleeing at "escape_speed" m/s (default 3, <see cref="HazardDefaults.EscapeSpeed"/>,
    ///   an assumption deliberately under run speed; the owner can test other speeds) who starts at the middle of the
    ///   side must be able to reach a row's gap before the wall moves: lead &gt;= (S/2 - G/2) / escape_speed (the worst
    ///   gap position is an end of the side). Cross: the straight-line distance from the middle of the region to the
    ///   farthest possible intersection, hypot((Sx - G)/2, (Sy - G)/2), over escape_speed. Ring: lead &gt;= r_ref /
    ///   escape_speed. Random: lead &gt;= h / escape_speed (step out of a lane). A shorter "lead" arg is RAISED to the
    ///   floor, never refused.
    /// </summary>
    public static class BallRules
    {
        /// <summary>Smallest centre-to-centre gap a player can slip through: 2 x (ball radius + player radius) + 0.5 m.</summary>
        public static float MinGap(BallParams p) => 2f * p.Reach + (float)HazardDefaults.BallsGapMargin;

        /// <summary>
        /// The numbers wave <paramref name="wave"/> (1-based) of the run uses: a full copy of <paramref name="p"/> with
        /// "speed", "lead" and "wave_gap" ramped over <c>p.Waves</c> by <see cref="HazardRamp.At"/> towards their "_end"
        /// keys. A run that gives no end key gets back exactly today's numbers. The ramped lead is still raised to the
        /// fair-lead floor where it is used (<see cref="LeadFor"/>), so a ramp can never take a wave below it.
        /// </summary>
        /// <summary>
        /// A wave planned under a live count (HazardRun.LiveCount): no wave may launch with more balls than the budget.
        /// The count-parameterised patterns (ring-out, ring-in, ring-alt, random, fan) are planned with count capped to
        /// the budget (a ring never below its own minimum of 3); row and cross have a size fixed by the wall geometry and
        /// cannot shrink, so they come back as given. A null live count returns <paramref name="p"/> itself: today's run.
        /// </summary>
        public static BallParams CapToLiveCount(BallParams p, int? liveCount)
        {
            if (!liveCount.HasValue)
                return p;

            var budget = BallBudget.For(liveCount, null).Value;

            if (p.Count <= budget)
                return p;

            var capped = (BallParams)p.CloneParams();
            capped.Count = budget;
            return capped;
        }

        public static BallParams ForWave(BallParams p, int wave) => new BallParams
        {
            BallRadius = p.BallRadius,
            BallRadiusGiven = p.BallRadiusGiven,
            EscapeSpeed = p.EscapeSpeed,
            PlayerRadius = p.PlayerRadius,
            HitLag = p.HitLag,
            Occupancy = p.Occupancy,
            Inset = p.Inset,
            Speed = (float)HazardRamp.At(p.Speed, p.SpeedEnd, wave, p.Waves),
            Lead = HazardRamp.At(p.Lead, p.LeadEnd, wave, p.Waves),
            FairLeadFloor = p.FairLeadFloor,
            Gap = p.Gap,
            Gaps = p.Gaps,
            NoOverlap = p.NoOverlap,
            OriginClear = p.OriginClear,
            DistinctDirs = p.DistinctDirs,
            Count = p.Count,
            Waves = p.Waves,
            // the gap AFTER the last wave is never used, so the ramp spans the Waves - 1 gaps that are: the last used gap is wave_gap_end
            WaveGap = HazardRamp.At(p.WaveGap, p.WaveGapEnd, wave, p.Waves - 1),
            Window = p.Window,
            Marker = p.Marker,
            MarkerScript = p.MarkerScript,
            RayMarkers = p.RayMarkers,
            Dir = p.Dir,
            Dirs = p.Dirs,
            Centre = p.Centre,
            Spread = p.Spread,
            Target = p.Target,
            SpeedEnd = p.SpeedEnd,
            LeadEnd = p.LeadEnd,
            WaveGapEnd = p.WaveGapEnd,
            // like wave_gap: the period AFTER the last wave is never used, so the ramp spans the Waves - 1 that are
            WaveEvery = p.WaveEvery.HasValue ? HazardRamp.At(p.WaveEvery.Value, p.WaveEveryEnd, wave, p.Waves - 1) : (double?)null,
            WaveEveryEnd = p.WaveEveryEnd,
            // the cap ramps over every wave (wave 1 the start, the last wave the end), rounded half away from zero
            MaxInFlight = p.MaxInFlight.HasValue
                ? Math.Max(1, (int)Math.Round(HazardRamp.At(p.MaxInFlight.Value, p.MaxInFlightEnd, wave, p.Waves), MidpointRounding.AwayFromZero))
                : (int?)null,
            MaxInFlightEnd = p.MaxInFlightEnd,
            MaxBalls = p.MaxBalls,
            Stream = p.Stream,
            Problem = p.Problem,
        };

        /// <summary>
        /// CADENCE MODE (wave_every): run seconds at which each wave launches, wave 1 at 0. Wave k+1 launches
        /// ForWave(k).WaveEvery after wave k. Pure, and depends on nothing a ball does: a launch never waits on an
        /// earlier wave. Empty when <paramref name="p"/> has no wave_every.
        /// </summary>
        public static IReadOnlyList<double> LaunchTimes(BallParams p)
        {
            var times = new List<double>();

            if (p == null || !p.Cadence)
                return times;

            var t = 0.0;

            for (var wave = 1; wave <= p.Waves; wave++)
            {
                times.Add(t);
                t += ForWave(p, wave).WaveEvery ?? 0.0;
            }

            return times;
        }

        /// <summary>The slowest speed any ball of the run can have: the lesser of "speed" and "speed_end".</summary>
        public static float SlowestSpeed(BallParams p) => p.SpeedEnd.HasValue ? Math.Min(p.Speed, p.SpeedEnd.Value) : p.Speed;

        public static BallParams Read(HazardArgs args, string pattern)
        {
            args ??= HazardArgs.Empty;

            string problem = null;

            var dir = (args.GetString("dir") ?? "random").Trim().ToLowerInvariant();

            if (dir != "random" && !(dir.Length == 1 && "nsew".IndexOf(dir[0]) >= 0))
                problem = $"dir '{args.GetString("dir")}' is not n, s, e, w or random";

            var dirs = (args.GetString("dirs") ?? "random").Trim().ToLowerInvariant();

            if (dirs != "random" && !ValidPair(dirs))
                problem ??= $"dirs '{args.GetString("dirs")}' must be two adjacent sides such as ne, nw, se, sw, or random";

            Vector2? centre = null;

            if (args.Has("cx") != args.Has("cy"))
            {
                problem ??= "give cx and cy together (landblock-local metres), or neither";
            }
            else if (args.Has("cx"))
            {
                if (TryNumber(args.GetString("cx"), out var cx) && TryNumber(args.GetString("cy"), out var cy))
                    centre = new Vector2(cx, cy);
                else
                    problem ??= "cx and cy must be numbers (landblock-local metres)";
            }

            var ring = BallPatterns.IsRing(pattern);
            var fan = pattern == BallPatterns.Fan;

            var target = (args.GetString("target") ?? "random").Trim().ToLowerInvariant();

            if (target != "random" && target != "nearest")
                problem ??= $"target '{args.GetString("target")}' must be random or nearest";

            // a stream is ONE random wave whose launches span "stream" seconds; any other pattern refuses it
            var stream = args.GetDouble("stream", 0.0, 0.0, HazardDefaults.BallsMaxWindow);

            if (stream > 0.0 && pattern != BallPatterns.Random)
                problem ??= $"stream={args.GetString("stream")} only works with the random pattern (a stream is one wave of random launches spread over that many seconds)";

            var streaming = stream > 0.0 && pattern == BallPatterns.Random;

            // fair_lead=0 (opt-in): no fair-lead floor, and the lead may be as short as 0
            var fairLead = args.GetBool("fair_lead", true);
            var minLead = fairLead ? HazardDefaults.BallsMinLead : 0.0;

            // the in-flight cap holds back a CADENCE launch; a sequential run has one wave in flight anyway
            if ((args.Has("max_in_flight") || args.Has("max_in_flight_end")) && !args.Has("wave_every"))
                problem ??= "max_in_flight only works with wave_every (it caps how many cadence waves are in flight at once)";

            if (args.Has("max_balls") && !args.Has("wave_every"))
                problem ??= "max_balls only works with wave_every (it caps how many balls the overlapping cadence waves have in flight at once)";

            return new BallParams
            {
                SpeedEnd = args.Has("speed_end") ? (float)args.GetDouble("speed_end", HazardDefaults.BallsSpeed, HazardDefaults.BallsMinSpeed, HazardDefaults.BallsMaxSpeed) : (float?)null,
                LeadEnd = args.Has("lead_end") ? args.GetDouble("lead_end", HazardDefaults.BallsLead, minLead, HazardDefaults.BallsMaxLead) : (double?)null,
                WaveGapEnd = args.Has("wave_gap_end") ? args.GetDouble("wave_gap_end", HazardDefaults.BallsWaveGap, 0.0, HazardDefaults.BallsMaxWaveGap) : (double?)null,
                WaveEvery = args.Has("wave_every") ? args.GetDouble("wave_every", HazardDefaults.BallsWaveGap, HazardDefaults.BallsMinWaveEvery, HazardDefaults.BallsMaxWaveEvery) : (double?)null,
                WaveEveryEnd = args.Has("wave_every_end") ? args.GetDouble("wave_every_end", HazardDefaults.BallsWaveGap, HazardDefaults.BallsMinWaveEvery, HazardDefaults.BallsMaxWaveEvery) : (double?)null,
                MaxInFlight = args.Has("max_in_flight") ? args.GetInt("max_in_flight", 1, 1, HazardDefaults.BallsMaxWaves) : (int?)null,
                MaxInFlightEnd = args.Has("max_in_flight_end") ? args.GetDouble("max_in_flight_end", 1.0, 1.0, HazardDefaults.BallsMaxWaves) : (double?)null,
                MaxBalls = args.Has("max_balls") ? args.GetInt("max_balls", BallBudget.Min, BallBudget.Min, BallBudget.Max) : (int?)null,
                Stream = stream,
                BallRadius = (float)args.GetDouble("ball_radius", HazardDefaults.RollingDeathRadius, 0.1, 6.0),
                BallRadiusGiven = args.Has("ball_radius"),
                EscapeSpeed = args.GetDouble(HazardDefaults.EscapeSpeedKey, HazardDefaults.EscapeSpeed, HazardDefaults.MinEscapeSpeed, HazardDefaults.MaxEscapeSpeed),
                PlayerRadius = (float)args.GetDouble(HazardDefaults.PlayerRadiusKey, HazardDefaults.PlayerRadius, HazardDefaults.MinPlayerRadius, HazardDefaults.MaxPlayerRadius),
                HitLag = args.GetDouble("hit_lag", DefaultHitLag, 0.0, MaxHitLag),
                Occupancy = args.GetDouble("occupancy", HazardDefaults.BallsRandomOccupancy, HazardDefaults.BallsMinOccupancy, HazardDefaults.BallsMaxOccupancy),
                Inset = (float)args.GetDouble(HazardDefaults.EdgeInsetKey, HazardDefaults.EdgeInset, HazardDefaults.MinEdgeInset, HazardDefaults.MaxEdgeInset),
                Speed = (float)args.GetDouble("speed", HazardDefaults.BallsSpeed, HazardDefaults.BallsMinSpeed, HazardDefaults.BallsMaxSpeed),
                Lead = args.GetDouble("lead", HazardDefaults.BallsLead, minLead, HazardDefaults.BallsMaxLead),
                FairLeadFloor = fairLead,
                Gap = (float)args.GetDouble("gap", HazardDefaults.BallsGap, HazardDefaults.BallsMinGapArg, HazardDefaults.BallsMaxGapArg),
                Gaps = args.GetInt("gaps", 1, 1, HazardDefaults.BallsMaxGaps),
                NoOverlap = args.GetBool("no_overlap", false),
                OriginClear = (float)args.GetDouble("origin_clear", 0.0, 0.0, HazardDefaults.BallsMaxOriginClear),
                DistinctDirs = args.GetBool("distinct_dirs", false),
                Count = fan
                    ? args.GetInt("count", HazardDefaults.BallsFanCount, 1, HazardDefaults.BallsFanMaxCount)
                    : args.GetInt("count", ring ? HazardDefaults.BallsRingCount : HazardDefaults.BallsRandomCount, HazardDefaults.BallsMinCount, HazardDefaults.BallsMaxCount),
                Spread = (float)args.GetDouble("spread", HazardDefaults.BallsFanSpread, 0.0, HazardDefaults.BallsFanMaxSpread),
                Target = target,
                Waves = streaming ? 1 : args.GetInt("waves", HazardDefaults.BallsWaves, 1, HazardDefaults.BallsMaxWaves),
                WaveGap = args.GetDouble("wave_gap", HazardDefaults.BallsWaveGap, 0.0, HazardDefaults.BallsMaxWaveGap),
                Window = streaming ? stream : args.GetDouble("window", HazardDefaults.BallsWindow, HazardDefaults.BallsMinWindow, HazardDefaults.BallsMaxWindow),
                Marker = args.GetBool("marker", true),
                MarkerScript = HazardToolRoutes.ParseScript(args.GetString("marker_script"), HazardDefaults.PillarScript),
                RayMarkers = args.GetBool("ray_markers", false),
                Dir = dir,
                Dirs = dirs,
                Centre = centre,
                Problem = problem,
            };
        }

        private static bool TryNumber(string s, out float value)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

        private static bool ValidPair(string s)
        {
            if (s.Length != 2)
                return false;

            var vertical = 0;
            var horizontal = 0;

            foreach (var ch in s)
            {
                if (ch == 'n' || ch == 's')
                    vertical++;
                else if (ch == 'e' || ch == 'w')
                    horizontal++;
                else
                    return false;
            }

            return vertical == 1 && horizontal == 1;
        }

        /// <summary>Every numeric arg the balls read; a given one that does not parse is REFUSED by Validate (never silently defaulted).</summary>
        public static readonly string[] NumberKeys =
        {
            "ball_radius", HazardDefaults.EscapeSpeedKey, HazardDefaults.PlayerRadiusKey, HazardDefaults.EdgeInsetKey, HazardDefaults.PctKey,
            "speed", "lead", "gap", "spread", "wave_gap", "window", HazardDefaults.BallHoverKey,
            "speed_end", "lead_end", "wave_gap_end", "stream", "wave_every", "wave_every_end", "max_in_flight_end",
            "origin_clear", "hit_lag", "occupancy",
        };

        public static readonly string[] WholeNumberKeys = { "count", "waves", "ball_wcid", "gaps", "max_in_flight", "max_balls" };

        /// <summary>
        /// Null when the run can start, else ONE line. Refuses every no-escape configuration (see the class doc for
        /// each bound) and anything the region cannot hold. <paramref name="pattern"/> is a member of
        /// <see cref="BallPatterns.All"/>.
        /// </summary>
        public static string Validate(HazardRegion region, HazardArgs args, string pattern)
        {
            if (region == null)
                return "no region";

            var notNumeric = (args ?? HazardArgs.Empty).FirstNonNumeric(NumberKeys, WholeNumberKeys);

            if (notNumeric != null)
                return notNumeric;

            // marker is a yes/no choice: a typo is refused, not silently read as the default
            var marker = args?.GetString("marker");

            if (marker != null && !new[] { "1", "true", "on", "yes", "0", "false", "off", "no" }.Contains(marker.Trim().ToLowerInvariant()))
                return $"marker='{marker}' is not one of true|false";

            var markerScript = args?.GetString("marker_script");

            if (markerScript != null && !HazardToolRoutes.TryParseScript(markerScript, out _))
                return $"marker_script='{markerScript}' is not a script id (decimal or 0x hex)";

            var fairLeadArg = args?.GetString("fair_lead");

            if (fairLeadArg != null && !new[] { "1", "true", "on", "yes", "0", "false", "off", "no" }.Contains(fairLeadArg.Trim().ToLowerInvariant()))
                return $"fair_lead='{fairLeadArg}' is not one of 1|0";

            var rayMarkers = args?.GetString("ray_markers");

            if (rayMarkers != null && !new[] { "1", "true", "on", "yes", "0", "false", "off", "no" }.Contains(rayMarkers.Trim().ToLowerInvariant()))
                return $"ray_markers='{rayMarkers}' is not one of 1|0";

            foreach (var key in new[] { "no_overlap", "distinct_dirs" })
            {
                var value = args?.GetString(key);

                if (value != null && !new[] { "1", "true", "on", "yes", "0", "false", "off", "no" }.Contains(value.Trim().ToLowerInvariant()))
                    return $"{key}='{value}' is not one of 1|0";
            }

            var p = Read(args, pattern);

            if (p.Problem != null)
                return p.Problem;

            var distinct = ValidateDistinctDirs(p, pattern);

            if (distinct != null)
                return distinct;

            string error;

            switch (pattern)
            {
                case BallPatterns.Row:
                    error = ValidateRows(region, p, SidesFor(p.Dir));
                    break;

                case BallPatterns.Cross:
                    error = ValidateRows(region, p, p.Dirs == "random" ? AllSides : SidesFor(p.Dirs[0].ToString(), p.Dirs[1].ToString()));
                    break;

                case BallPatterns.RingOut:
                case BallPatterns.RingIn:
                case BallPatterns.RingAlt:
                    error = ValidateRing(region, p);
                    break;

                case BallPatterns.Random:
                    error = ValidateRandom(region, p);
                    break;

                case BallPatterns.Fan:
                    error = ValidateFan(region, p);
                    break;

                default:
                    return $"unknown ball pattern '{pattern}'";
            }

            if (error != null)
                return error;

            return ValidateRegionHolds(region, p, pattern);
        }

        /// <summary>
        /// distinct_dirs=1 needs a wall to draw from whenever a wave launches. It is refused unless the run is a row with
        /// dir=random whose in-flight cap never exceeds <see cref="HazardDefaults.BallsDistinctRowMaxInFlight"/> (4 walls,
        /// one per wave), or a cross with dirs=random whose cap never exceeds
        /// <see cref="HazardDefaults.BallsDistinctCrossMaxInFlight"/> (2 north/south and 2 east/west walls, one of each
        /// per wave). The cap is max_in_flight (with wave_every), checked at every wave of its ramp. Null when off.
        /// </summary>
        private static string ValidateDistinctDirs(BallParams p, string pattern)
        {
            if (!p.DistinctDirs)
                return null;

            int limit;

            if (pattern == BallPatterns.Row)
            {
                if (p.Dir != "random")
                    return $"distinct_dirs=1 needs dir=random (got dir={p.Dir}): it draws each row's wall from the walls no row in flight is using";

                limit = HazardDefaults.BallsDistinctRowMaxInFlight;
            }
            else if (pattern == BallPatterns.Cross)
            {
                if (p.Dirs != "random")
                    return $"distinct_dirs=1 needs dirs=random (got dirs={p.Dirs}): it draws each cross's walls from the walls no cross in flight is using";

                limit = HazardDefaults.BallsDistinctCrossMaxInFlight;
            }
            else
            {
                return $"distinct_dirs=1 only works with the row and cross patterns (got {pattern})";
            }

            if (!p.Cadence || !p.MaxInFlight.HasValue)
                return $"distinct_dirs=1 needs wave_every and max_in_flight <= {limit} for a {pattern} (only overlapping waves can share a wall, and the cap keeps a wall free)";

            var cap = MaxCap(p);

            if (cap > limit)
                return $"distinct_dirs=1 allows max_in_flight up to {limit} for a {pattern} (got {cap}): more waves in flight than that cannot each have their own wall";

            return null;
        }

        /// <summary>The largest in-flight cap any wave of the run has (max_in_flight ramped and rounded), or 0 when uncapped.</summary>
        public static int MaxCap(BallParams p)
        {
            var cap = 0;

            if (p.MaxInFlight.HasValue)
                for (var wave = 1; wave <= p.Waves; wave++)
                    cap = Math.Max(cap, ForWave(p, wave).MaxInFlight ?? 0);

            return cap;
        }

        /// <summary>
        /// What the region itself must allow, whatever the pattern: at least one ball path (checked on the middle lane
        /// of every side in play, never on a random draw), no exclusion touching a row's start or end line (a safe
        /// spot), and no more than <see cref="HazardDefaults.BallsMaxRoutes"/> balls in one wave.
        /// </summary>
        private static string ValidateRegionHolds(HazardRegion region, BallParams p, string pattern)
        {
            var tooSmall = F($"region is too small to roll a ball across once edge_inset={p.Inset:0.##} m is kept clear of its edge");

            if (BallPatterns.IsRing(pattern))
            {
                var ray = ShortestRay(region, p.Centre ?? region.Center, p.Inset);

                if (ray < HazardDefaults.BallsMinLane)
                    return tooSmall;

                // origin_clear shortens every ray from the centre end: the shortest must still hold a lane
                if (p.OriginClear > 0f && ray - p.OriginClear < HazardDefaults.BallsMinLane)
                    return F($"origin_clear={p.OriginClear:0.##} m leaves the shortest ring ray {ray - p.OriginClear:0.##} m of its {ray:0.##} m, under the {HazardDefaults.BallsMinLane:0.##} m a ball needs; lower origin_clear");

                return null;
            }

            if (pattern == BallPatterns.Fan && p.OriginClear > 0f && BallPatterns.PlanFanAlong(region, p, 0.0).Routes.Count == 0)
                return F($"origin_clear={p.OriginClear:0.##} m leaves no fan ray a {HazardDefaults.BallsMinLane:0.##} m lane in this region; lower origin_clear");

            if (pattern == BallPatterns.Fan)
                return BallPatterns.PlanFanAlong(region, p, 0.0).Routes.Count == 0 ? tooSmall : null;

            var sides = pattern == BallPatterns.Row ? SidesFor(p.Dir)
                : pattern == BallPatterns.Cross ? (p.Dirs == "random" ? AllSides : SidesFor(p.Dirs[0].ToString(), p.Dirs[1].ToString()))
                : AllSides;

            var walls = new Dictionary<BallSide, int>();

            foreach (var side in sides)
            {
                var (lo, hi) = BallPatterns.LateralRange(region, side, p);

                if (pattern != BallPatterns.Random)
                {
                    var touch = ExclusionTouch(region, side, p, lo, hi);

                    if (touch != null)
                        return touch;

                    walls[side] = p.NoOverlap && (pattern == BallPatterns.Row || pattern == BallPatterns.Cross)
                        ? BallPatterns.NoOverlapWall(lo, hi, BallPatterns.EvenCentres(lo, hi, p.Gaps), p.Gap, p.Reach).Positions.Count
                        : BallPatterns.WallPositions(lo, hi, (lo + hi) * 0.5f, p.Gap, p.Reach).Count;
                }

                if (!BallPatterns.TryLane(region, side, (lo + hi) * 0.5f, p, out _, out _))
                    return tooSmall;
            }

            if (pattern != BallPatterns.Random)
            {
                var total = pattern == BallPatterns.Row
                    ? walls.Values.Max()
                    : walls.Where(w => BallPatterns.LateralIsX(w.Key)).Select(w => w.Value).DefaultIfEmpty(0).Max()
                      + walls.Where(w => !BallPatterns.LateralIsX(w.Key)).Select(w => w.Value).DefaultIfEmpty(0).Max();

                if (total > HazardDefaults.BallsMaxRoutes)
                    return F($"a wave of {total} balls is over the cap of {HazardDefaults.BallsMaxRoutes}; raise ball_radius or player_radius, or use a smaller region");
            }

            return null;
        }

        /// <summary>
        /// Balls pass THROUGH exclusions but never leave a safe spot (HazardRegion). A row lane whose start or end
        /// falls in an exclusion is slid inward (BallPatterns.Trim), so its ball would not cover the edge it
        /// started from: refuse, naming the side. Compares every sampled lane trimmed against untrimmed.
        /// </summary>
        private static string ExclusionTouch(HazardRegion region, BallSide side, BallParams p, float lo, float hi)
        {
            if (region.Exclusions.Count == 0)
                return null;

            var step = Math.Max(0.25f, p.Reach * 0.5f);

            for (var lat = lo; lat <= hi + 1e-3f; lat += step)
            {
                if (!BallPatterns.RawLane(region, side, lat, p, out var start, out var end))
                    continue;

                var s2 = start;
                var e2 = end;

                if (!BallPatterns.Trim(region, ref s2, ref e2) || Vector2.Distance(start, s2) > 0.01f || Vector2.Distance(end, e2) > 0.01f)
                    return F($"exclusion touches the {BallPatterns.Name(side)} start/end line; that area would be a safe spot");
            }

            return null;
        }

        private static readonly BallSide[] AllSides ={ BallSide.North, BallSide.East, BallSide.South, BallSide.West };

        internal static BallSide[] SidesFor(params string[] letters)
        {
            var list = new List<BallSide>();

            foreach (var l in letters)
            {
                if (l == "random")
                    return AllSides;

                list.Add(BallPatterns.ParseSide(l[0]));
            }

            return list.ToArray();
        }

        private static string ValidateRows(HazardRegion region, BallParams p, IEnumerable<BallSide> sides)
        {
            var w = MinGap(p);

            if (p.Gap < w)
                return F($"gap {p.Gap:0.##} m is too narrow: a ball (radius {p.BallRadius:0.##}) and a player (radius {p.PlayerRadius:0.##}) need at least {w:0.##} m between the flank balls' centres (2 x reach + 0.5)");

            foreach (var side in sides)
            {
                var span = BallPatterns.LateralSpan(region, side, p);
                var need = WallNeed(p);

                if (p.NoOverlap && span < need)
                    return F($"no_overlap=1: {p.Gaps} opening(s) of {p.Gap:0.##} m do not fit the {BallPatterns.Name(side)} side: it spans {span:0.##} m between the insets, and {p.Gaps} x gap + {p.Gaps + 1} x one pitch (2 x reach + {HazardDefaults.BallsNoOverlapMargin:0.##} = {NoOverlapPitch(p.Reach):0.##} m) = {need:0.##} m; lower gaps or gap");

                if (span < need && p.Gaps > 1)
                    return F($"gaps={p.Gaps} openings of {p.Gap:0.##} m do not fit the {BallPatterns.Name(side)} side: it spans {span:0.##} m between the insets, and {p.Gaps} x gap + {p.Gaps + 1} x one ball-width (2 x reach = {2f * p.Reach:0.##} m) = {need:0.##} m; lower gaps or gap");

                if (span < need)
                    return F($"gap {p.Gap:0.##} m does not fit the {BallPatterns.Name(side)} side: it spans {span:0.##} m between the insets, and gap + 2 x reach = {need:0.##} m");
            }

            return null;
        }

        /// <summary>
        /// The lateral span a wall needs. One opening: gap + one ball-width (2 x reach), as always. N openings: N x gap
        /// plus one ball-width of wall between each pair of neighbours and at both ends, N x gap + (N + 1) x 2 x reach.
        /// </summary>
        public static float WallNeed(BallParams p)
            => p.NoOverlap ? p.Gaps * p.Gap + (p.Gaps + 1) * NoOverlapPitch(p.Reach)
                : p.Gaps <= 1 ? p.Gap + 2f * p.Reach : p.Gaps * p.Gap + (p.Gaps + 1) * 2f * p.Reach;

        /// <summary>no_overlap's wall pitch: 2 x reach + <see cref="HazardDefaults.BallsNoOverlapMargin"/>, so neighbouring reach discs never overlap and a standing player is never hit by two neighbours (the 1 cm unhit strip midway is intended; see <see cref="BallPatterns.NoOverlapWall"/>).</summary>
        public static float NoOverlapPitch(float reach) => 2f * reach + (float)HazardDefaults.BallsNoOverlapMargin;

        /// <summary>The largest ring N whose chord at radius <paramref name="r"/> is at least <paramref name="w"/>: pi / asin(w / 2r). 0 when even two balls do not fit.</summary>
        public static int MaxRingCount(double r, double w)
        {
            var ratio = w / (2.0 * r);

            if (!(ratio < 1.0) || !(r > 0.0))
                return 0;

            return (int)Math.Floor(Math.PI / Math.Asin(ratio) + 1e-9);
        }

        /// <summary>R_min: the shortest a ring ray can be from <paramref name="centre"/> (the nearest wall, less the inset). Never negative.</summary>
        public static float ShortestRay(HazardRegion region, Vector2 centre, float inset)
            => Math.Max(0f, region.DistanceToEdge(centre.X, centre.Y) - inset);

        /// <summary>The chord between adjacent rays of an N ring at radius r: 2 r sin(pi / N).</summary>
        public static double ChordAt(double r, int n) => 2.0 * r * Math.Sin(Math.PI / Math.Max(1, n));

        /// <summary>The radius where an N ring's chord first reaches <paramref name="w"/>: w / (2 sin(pi / N)).</summary>
        public static double LaneRadius(double w, int n) => w / (2.0 * Math.Sin(Math.PI / Math.Max(2, n)));

        private static string ValidateRing(HazardRegion region, BallParams p)
        {
            if (p.Count < 3)
                return $"a ring needs count >= 3 (got {p.Count})";

            var c = p.Centre ?? region.Center;

            if (!region.Contains(c.X, c.Y))
                return F($"ring centre ({c.X:0.##}, {c.Y:0.##}) is outside the region");

            if (region.InExclusion(c.X, c.Y))
                return F($"ring centre ({c.X:0.##}, {c.Y:0.##}) is inside an exclusion zone; give cx and cy outside it");

            var rMin = ShortestRay(region, c, p.Inset);
            var rEsc = HazardDefaults.BallsRingEscapeFraction * rMin;
            var w = MinGap(p);
            var chord = ChordAt(rEsc, p.Count);

            if (chord < w)
                return F($"a ring of {p.Count} balls leaves a {chord:0.##} m chord at r={rEsc:0.##} m (0.6 x the shortest ray {rMin:0.##} m), under the {w:0.##} m a player needs; use count <= {MaxRingCount(rEsc, w)} or a larger region");

            return null;
        }

        /// <summary>
        /// FAN SIDESTEP RULE (the one escape rule for the fan). The volley is aimed AT a player, so the gap between
        /// adjacent balls is no escape (it can be zero: count 1, spread 0). The escape is to move out sideways. With
        /// t = spread / 2 (capped under 90 degrees), the outer ray passes d sin(t) from the aim line at distance d,
        /// and the player must end up a perpendicular reach (+0.5 m) clear of it, which is (d sin t + reach) / cos t
        /// + 0.5 m of travel, at escape_speed (<see cref="FanLead"/>). The lead is RAISED to that time for the real d of
        /// each wave; Validate checks the worst case d = the region's longest extent and refuses a fan whose sidestep
        /// would need more than the 30 s lead cap, plus an origin that is outside the region or inside an exclusion.
        /// </summary>
        private static string ValidateFan(HazardRegion region, BallParams p)
        {
            var o = p.Centre ?? region.Center;

            if (!region.Contains(o.X, o.Y))
                return F($"fan origin ({o.X:0.##}, {o.Y:0.##}) is outside the region");

            if (region.InExclusion(o.X, o.Y))
                return F($"fan origin ({o.X:0.##}, {o.Y:0.##}) is inside an exclusion zone; give cx and cy outside it");

            // the sidestep cap is a FAIRNESS bound: with fair_lead=0 the fan is not held to it
            var need = FanLead(p.Spread, BallPatterns.LongestExtent(region, p), p.Reach, p.EscapeSpeed);

            if (p.FairLeadFloor && need > HazardDefaults.BallsMaxLead)
                return F($"a {p.Spread:0.#} degree fan is too wide to sidestep: at {BallPatterns.LongestExtent(region, p):0.#} m it needs {need:0.#} s of lead at {p.EscapeSpeed:0.#} m/s, over the {HazardDefaults.BallsMaxLead:0.#} s cap; lower spread");

            return null;
        }

        /// <summary>
        /// Seconds a player on the aim line needs to leave a fan at distance <paramref name="distance"/>:
        /// ((d sin t + reach) / cos t + 0.5) / escape_speed with t = spread / 2, capped at 85 degrees so the formula
        /// stays finite.
        /// </summary>
        public static double FanLead(double spreadDegrees, double distance, double reach, double escapeSpeed)
        {
            var t = Math.Min(spreadDegrees * Math.PI / 360.0, 85.0 * Math.PI / 180.0);

            return ((Math.Max(0.0, distance) * Math.Sin(t) + reach) / Math.Cos(t) + HazardDefaults.BallsGapMargin) / Math.Max(0.1, escapeSpeed);
        }

        private static string ValidateRandom(HazardRegion region, BallParams p)
        {
            var sMin = Math.Min(BallPatterns.LateralSpan(region, BallSide.North, p), BallPatterns.LateralSpan(region, BallSide.East, p));

            if (sMin < 2f * p.Reach)
                return F($"region's narrowest side spans {sMin:0.##} m between the insets, under one ball lane ({2f * p.Reach:0.##} m)");

            var lMax = BallPatterns.LongestExtent(region, p);
            // a ramp makes the slowest ball the worst case: check occupancy at the lesser of speed and speed_end
            var slowest = SlowestSpeed(p);
            var m = p.Count / p.Window * (lMax / slowest);
            var limit = p.Occupancy * sMin / (2.0 * p.Reach);

            if (m > limit)
                return F($"random launch rate is too high: about {m:0.#} balls are in flight at once ({p.Count} per {p.Window:0.#} s, {lMax:0.#} m at {slowest:0.#} m/s) and the region fits {limit:0.#}; lower count, raise window, speed or occupancy");

            return null;
        }

        /// <summary>
        /// The lead the run really uses: the "lead" arg, raised to the fair-lead floor for the pattern (see the class
        /// doc). At least <see cref="HazardDefaults.BallsMinLead"/>, except with fair_lead=0: then it is the "lead" arg as
        /// written, which may be 0.
        /// </summary>
        public static double EffectiveLead(HazardRegion region, string pattern, BallParams p)
            => Math.Max(p.Lead, FairLead(region, pattern, p));

        /// <summary>
        /// THE lead a wave uses, for its markers' lifetime and its launch schedule alike. A fan passes the real
        /// distance to its target (<paramref name="fanDistance"/>) and gets the sidestep time for THAT distance;
        /// every other pattern (and a fan with no distance yet) gets <see cref="EffectiveLead"/>.
        /// </summary>
        public static double LeadFor(HazardRegion region, string pattern, BallParams p, double? fanDistance)
            => !p.FairLeadFloor ? p.Lead
                : pattern == BallPatterns.Fan && fanDistance.HasValue
                ? Math.Max(Math.Max(p.Lead, HazardDefaults.BallsMinLead), FanLead(p.Spread, fanDistance.Value, p.Reach, p.EscapeSpeed))
                : EffectiveLead(region, pattern, p);

        /// <summary>A lead shorter than this is announced as no lead at all: the balls move the moment they appear.</summary>
        public const double AnnouncedLeadMin = 0.05;

        /// <summary>
        /// The end of a wave announcement, after its summary: ". They move in N s" plus the marker note, said only for a
        /// lead of at least <see cref="AnnouncedLeadMin"/>. A shorter lead (fair_lead=0 lead=0, Grael's fan) drops that
        /// clause, so the announcement never says "They move in 0 s"; the marker note, when there is one, stays.
        /// </summary>
        public static string MoveClause(double lead, string note)
            => lead >= AnnouncedLeadMin
                ? FormattableString.Invariant($". They move in {lead:0.#} s{note}.")
                : (note ?? "") + ".";

        /// <summary>How long a marker pillar stands: the lead plus a second, so it outlives the first roll.</summary>
        public static double MarkerSeconds(double lead) => lead + 1.0;

        /// <summary>The ball's spawn scale: 1 (the weenie as authored) unless ball_radius was given, then radius / 1.8 so the model matches the hit radius.</summary>
        public static float SpawnScale(BallParams p) => p.BallRadiusGiven ? p.BallRadius / HazardDefaults.RollingDeathRadius : 1f;

        public static double FairLead(HazardRegion region, string pattern, BallParams p)
        {
            // fair_lead=0: no floor at all (the lead arg is used as written, down to 0)
            if (!p.FairLeadFloor)
                return 0.0;

            var walk = p.EscapeSpeed;
            double floor;

            switch (pattern)
            {
                case BallPatterns.Row:
                {
                    double span = 0.0;

                    foreach (var s in SidesFor(p.Dir))
                        span = Math.Max(span, BallPatterns.LateralSpan(region, s, p));

                    // gaps > 1 keeps this ONE-opening floor: with more openings (a ball-width of wall at the ends) the
                    // nearest one is never farther from the middle than a lone opening at an end, so it never rises
                    floor = Math.Max(0.0, span / 2.0 - p.Gap / 2.0) / walk;
                    break;
                }

                case BallPatterns.Cross:
                {
                    // the intersection can be anywhere in the Sx x Sy box shrunk by half a gap: walk the straight line to the far corner
                    var dx = Math.Max(0.0, (BallPatterns.LateralSpan(region, BallSide.North, p) - p.Gap) / 2.0);
                    var dy = Math.Max(0.0, (BallPatterns.LateralSpan(region, BallSide.East, p) - p.Gap) / 2.0);
                    floor = Math.Sqrt(dx * dx + dy * dy) / walk;
                    break;
                }

                case BallPatterns.RingOut:
                case BallPatterns.RingIn:
                case BallPatterns.RingAlt:
                    floor = LaneRadius(MinGap(p), Math.Max(2, p.Count)) / walk;
                    break;

                case BallPatterns.Fan:
                    floor = FanLead(p.Spread, BallPatterns.LongestExtent(region, p), p.Reach, walk);
                    break;

                default:
                    floor = p.Reach / walk;
                    break;
            }

            return Math.Max(HazardDefaults.BallsMinLead, floor);
        }

        /// <summary>Seconds from a wave's start to its last ball's ideal arrival (no settle).</summary>
        public static double IdealSeconds(BallWave wave, float speed, double lead)
        {
            var last = 0.0;

            foreach (var r in wave.Routes)
                last = Math.Max(last, r.Delay + lead + LegSeconds(r, speed));

            return last;
        }

        /// <summary>Ideal arrival plus a flat 1 s: the ideal clock the first version ended waves on. The wave now ends on its balls; see <see cref="FallbackSeconds"/>.</summary>
        public static double WaveSeconds(BallWave wave, float speed, double lead) => IdealSeconds(wave, speed, lead) + 1.0;

        /// <summary>The settle the timeout fallback allows beyond the ideal time: max(1 s, 3 driver ticks), because spawn, launch and leg end each land on a tick.</summary>
        public static double Settle(double tickSeconds) => Math.Max(1.0, 3.0 * tickSeconds);

        /// <summary>
        /// When the fallback may force a wave to end: ideal arrival + <see cref="Settle"/> + <paramref name="hitLag"/>
        /// (<see cref="BallParams.HitLag"/>) + one more tick when hitLag &gt; 0, so a healthy ball is never written off
        /// while its lagged tail is still being swept (the tail finishes on the first tick at or after leg end + hit_lag,
        /// which a slow tick can push almost a whole tick later). hitLag 0 is the old deadline.
        /// </summary>
        public static double FallbackSeconds(BallWave wave, float speed, double lead, double tickSeconds, double hitLag = 0.0)
            => IdealSeconds(wave, speed, lead) + Settle(tickSeconds) + (hitLag > 0.0 ? hitLag + tickSeconds : 0.0);

        /// <summary>args "hit_lag" default, seconds (<see cref="BallParams.HitLag"/>). In code on purpose: not in the hazard_mech_balls string.</summary>
        public const double DefaultHitLag = 0.15;

        /// <summary>args "hit_lag" upper clamp, seconds (the lower is 0, no compensation).</summary>
        public const double MaxHitLag = 0.5;

        /// <summary>The leg time at the route's own speed when it has one (a stream), else at <paramref name="speed"/>; never under 0.5 s (the movement test's rule).</summary>
        public static double LegSeconds(BallRoute r, float speed) => HazardMoveTestMechanic.LegSeconds(r.From, r.To, r.Speed > 0f ? r.Speed : speed);

        private static string F(FormattableString s) => FormattableString.Invariant(s);
    }

    /// <summary>
    /// PURE geometry for the balls mechanic (shared invariant 6): every route comes from the region and the passed-in
    /// Random alone, so every pattern works on any rect or circle, outdoors or in a dungeon, wherever the region sits.
    ///
    /// COMPASS ON ANY SHAPE. North is +y, East +x. A rect's sides are its edges; a circle's side is the arc facing
    /// that way, and a row from it spans the full chord. Every lane runs parallel to its axis, from the near edge to
    /// the far edge AT THAT LATERAL POSITION (on a circle the lane is the chord there, so lanes near the rim are short
    /// and a lane shorter than <see cref="HazardDefaults.BallsMinLane"/> is dropped; the lateral range stops where lanes
    /// reach that length). All points stay inside the shape, at least edge_inset from the edge along the travel axis;
    /// across the lane a rect keeps min(edge_inset, reach / 2) so a row's end balls still cover the strip by the wall.
    /// A start or end that falls in an exclusion is slid along its own path until placeable; a path that can no longer
    /// reach <see cref="HazardDefaults.BallsMinLane"/> is dropped.
    /// </summary>
    public static class BallPatterns
    {
        public const string RingOut = "ring-out";
        public const string RingIn = "ring-in";
        public const string Row = "row";
        public const string Random = "random";
        public const string Cross = "cross";
        public const string Fan = "fan";

        /// <summary>
        /// One ring series whose waves ALTERNATE: odd waves are a ring-out starburst, even waves a ring-in convergence,
        /// each with its own random rotation. Validated, laned and fair-led exactly as ring-out and ring-in are.
        /// </summary>
        public const string RingAlt = "ring-alt";

        /// <summary>The registry's pattern list. The first is the default.</summary>
        public static readonly IReadOnlyList<string> All = new[] { RingOut, RingIn, Row, Random, Cross, Fan, RingAlt };

        /// <summary>ring-out, ring-in and ring-alt: the patterns that share the ring rules.</summary>
        public static bool IsRing(string pattern) => pattern == RingOut || pattern == RingIn || pattern == RingAlt;

        /// <summary>Whether wave <paramref name="wave"/> (1-based) of a ring-alt run rolls outward: odd waves out, even waves in.</summary>
        public static bool RingAltOutward(int wave) => wave % 2 != 0;

        public static string Name(BallSide s) => s.ToString().ToLowerInvariant();

        public static BallSide ParseSide(char letter)
        {
            switch (char.ToLowerInvariant(letter))
            {
                case 'n': return BallSide.North;
                case 'e': return BallSide.East;
                case 's': return BallSide.South;
                default: return BallSide.West;
            }
        }

        public static BallSide Opposite(BallSide s) => (BallSide)(((int)s + 2) % 4);

        /// <summary>True for North/South: the lane runs along y and the lateral axis is x.</summary>
        public static bool LateralIsX(BallSide s) => s == BallSide.North || s == BallSide.South;

        // ---- the plan --------------------------------------------------------------------------------------------

        /// <summary>
        /// One wave of <paramref name="pattern"/> on <paramref name="region"/>. Draws only from <paramref name="rng"/>.
        /// <paramref name="wave"/> (1-based) matters only to ring-alt, which alternates direction by wave.
        /// <paramref name="busyWalls"/> matters only to a row or cross with distinct_dirs=1: the walls waves still in
        /// flight are using, which this wave's wall(s) are then drawn from outside of.
        /// </summary>
        public static BallWave Plan(HazardRegion region, string pattern, BallParams p, System.Random rng, int wave = 1, IReadOnlyCollection<BallSide> busyWalls = null)
        {
            var busy = p.DistinctDirs ? busyWalls : null;

            switch (pattern)
            {
                case RingOut: return PlanRing(region, p, rng, outward: true);
                case RingIn: return PlanRing(region, p, rng, outward: false);
                case RingAlt: return PlanRing(region, p, rng, outward: RingAltOutward(wave));
                case Row: return PlanRow(region, p, rng, busy);
                case Cross: return PlanCross(region, p, rng, busy);
                case Random: return PlanRandom(region, p, rng);
                case Fan: return PlanFanAlong(region, p, 0.0); // no player known here: aim along +x (Validate's dry run, tests)
                default: return new BallWave { Summary = "unknown pattern" };
            }
        }

        // ---- lateral / lane geometry -----------------------------------------------------------------------------

        private static float CircleUsable(HazardRegion r, BallParams p) => Math.Max(0f, r.Radius - p.Inset);

        /// <summary>The lateral range lane positions may take for a row from <paramref name="side"/>.</summary>
        public static (float Lo, float Hi) LateralRange(HazardRegion r, BallSide side, BallParams p)
        {
            var lx = LateralIsX(side);

            if (r.Shape == HazardShape.Circle)
            {
                var ru = CircleUsable(r, p);
                var half = (float)HazardDefaults.BallsMinLane * 0.51f; // lanes at the lateral limit come out a hair over the minimum, never a hair under
                var rl = (float)Math.Sqrt(Math.Max(0f, ru * ru - half * half));
                var c = lx ? r.CenterX : r.CenterY;
                return (c - rl, c + rl);
            }

            var latInset = Math.Min(p.Inset, p.Reach * 0.5f);
            var lo = (lx ? r.MinX : r.MinY) + latInset;
            var hi = (lx ? r.MaxX : r.MaxY) - latInset;

            if (lo > hi)
                lo = hi = (lo + hi) * 0.5f;

            return (lo, hi);
        }

        public static float LateralSpan(HazardRegion r, BallSide side, BallParams p)
        {
            var (lo, hi) = LateralRange(r, side, p);
            return hi - lo;
        }

        /// <summary>The region's longest straight extent along either axis (the farthest a ball can roll).</summary>
        public static float LongestExtent(HazardRegion r, BallParams p)
            => r.Shape == HazardShape.Circle
                ? 2f * CircleUsable(r, p)
                : Math.Max(r.MaxX - r.MinX, r.MaxY - r.MinY) - 2f * p.Inset;

        /// <summary>
        /// The start and end of the lane at lateral position <paramref name="lat"/> for a row from
        /// <paramref name="side"/>, slid out of any exclusion (<see cref="Trim"/>). False when the lane is under the
        /// minimum length.
        /// </summary>
        public static bool TryLane(HazardRegion r, BallSide side, float lat, BallParams p, out Vector2 start, out Vector2 end)
            => RawLane(r, side, lat, p, out start, out end) && Trim(r, ref start, ref end);

        /// <summary>The lane exactly as the shape gives it, exclusions ignored. False when it is under the minimum length.</summary>
        public static bool RawLane(HazardRegion r, BallSide side, float lat, BallParams p, out Vector2 start, out Vector2 end)
        {
            var lx = LateralIsX(side);
            float a0, a1;

            if (r.Shape == HazardShape.Circle)
            {
                var ru = CircleUsable(r, p);
                var d = lat - (lx ? r.CenterX : r.CenterY);
                var half = (float)Math.Sqrt(Math.Max(0f, ru * ru - d * d));
                var c = lx ? r.CenterY : r.CenterX;
                a0 = c - half;
                a1 = c + half;
            }
            else
            {
                a0 = (lx ? r.MinY : r.MinX) + p.Inset;
                a1 = (lx ? r.MaxY : r.MaxX) - p.Inset;

                if (a0 > a1)
                    a0 = a1 = (a0 + a1) * 0.5f;
            }

            var fromHigh = side == BallSide.North || side == BallSide.East;
            var s = fromHigh ? a1 : a0;
            var e = fromHigh ? a0 : a1;

            start = lx ? new Vector2(lat, s) : new Vector2(s, lat);
            end = lx ? new Vector2(lat, e) : new Vector2(e, lat);

            return Math.Abs(a1 - a0) >= HazardDefaults.BallsMinLane;
        }

        /// <summary>
        /// INVARIANT 10 (nothing spawns in a solid): the first point on the lane from <paramref name="from"/> towards
        /// <paramref name="to"/>, at most <paramref name="maxDistance"/> along it, for which <paramref name="hasCell"/>
        /// holds, probing every <paramref name="step"/> metres. Null when none does (the caller logs the hole).
        /// </summary>
        public static Vector2? NudgeStart(Vector2 from, Vector2 to, Func<Vector2, bool> hasCell, float maxDistance, float step = 0.5f)
        {
            if (hasCell(from))
                return from;

            var len = Vector2.Distance(from, to);

            if (len < 1e-4f || !(step > 0f))
                return null;

            var dir = (to - from) / len;
            var limit = Math.Min(len, Math.Max(0f, maxDistance));

            for (var d = step; d <= limit + 1e-4f; d += step)
            {
                var pt = from + dir * d;

                if (hasCell(pt))
                    return pt;
            }

            return null;
        }

        /// <summary>
        /// Slides a path's ends along the path until both are placeable (never in an exclusion). False when what is
        /// left is shorter than the minimum lane. A region with no exclusion only checks the length.
        /// </summary>
        public static bool Trim(HazardRegion r, ref Vector2 a, ref Vector2 b)
        {
            var len = Vector2.Distance(a, b);

            if (len < HazardDefaults.BallsMinLane)
                return false;

            if (r.Exclusions.Count == 0)
                return true;

            var dir = (b - a) / len;
            var s0 = 0f;
            var s1 = len;

            while (s0 <= s1 && !r.IsPlaceable(a.X + dir.X * s0, a.Y + dir.Y * s0))
                s0 += 0.5f;

            while (s1 >= s0 && !r.IsPlaceable(a.X + dir.X * s1, a.Y + dir.Y * s1))
                s1 -= 0.5f;

            if (s1 - s0 < HazardDefaults.BallsMinLane)
                return false;

            var origin = a;
            a = origin + dir * s0;
            b = origin + dir * s1;
            return true;
        }

        /// <summary>How far a ray from <paramref name="origin"/> along unit <paramref name="dir"/> runs before it meets the region edge pulled in by <paramref name="inset"/>.</summary>
        public static float RayExit(HazardRegion r, Vector2 origin, Vector2 dir, float inset)
        {
            if (r.Shape == HazardShape.Circle)
            {
                var ru = Math.Max(0f, r.Radius - inset);
                var o = origin - r.Center;
                var b = Vector2.Dot(o, dir);
                var cterm = o.LengthSquared() - ru * ru;
                var disc = b * b - cterm;

                return disc < 0f ? 0f : Math.Max(0f, -b + (float)Math.Sqrt(disc));
            }

            var x0 = r.MinX + inset;
            var x1 = r.MaxX - inset;
            var y0 = r.MinY + inset;
            var y1 = r.MaxY - inset;

            if (x0 > x1 || y0 > y1)
                return 0f;

            var t = float.PositiveInfinity;

            if (dir.X > 1e-6f)
                t = Math.Min(t, (x1 - origin.X) / dir.X);
            else if (dir.X < -1e-6f)
                t = Math.Min(t, (x0 - origin.X) / dir.X);

            if (dir.Y > 1e-6f)
                t = Math.Min(t, (y1 - origin.Y) / dir.Y);
            else if (dir.Y < -1e-6f)
                t = Math.Min(t, (y0 - origin.Y) / dir.Y);

            return float.IsInfinity(t) ? 0f : Math.Max(0f, t);
        }

        // ---- rings -----------------------------------------------------------------------------------------------

        private static BallWave PlanRing(HazardRegion r, BallParams p, System.Random rng, bool outward)
        {
            var wave = new BallWave();
            var c = p.Centre ?? r.Center;
            var n = Math.Max(3, p.Count);
            var phase = rng.NextDouble() * 2.0 * Math.PI / n;

            for (var i = 0; i < n; i++)
            {
                var angle = phase + 2.0 * Math.PI * i / n;
                var dir = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                var t = RayExit(r, c, dir, p.Inset);
                var edge = c + dir * t;

                // origin_clear (opt-in): the centre end of the ray is pulled out along it, so a ring-out starts there and a
                // ring-in stops there. 0 (the default) keeps the centre itself.
                if (p.OriginClear > 0f && p.OriginClear >= t)
                    continue; // the clearance swallows the whole ray (Validate refuses a region where that can happen)

                var near = p.OriginClear > 0f ? c + dir * p.OriginClear : c;
                var from = outward ? near : edge;
                var to = outward ? edge : near;

                if (!Trim(r, ref from, ref to))
                    continue;

                wave.Routes.Add(new BallRoute(from, to, 0.0, outward ? "ring-out" : "ring-in"));

                // ray_markers=1 (opt-in, review C11): the fan's telegraph on every ray, a pillar every
                // BallsFanMarkerSpacing m out from the centre along the stretch the ball really rolls, so the lead shows
                // where each ball will run. Off (the default, every /hazard ring run) nothing is added.
                if (p.RayMarkers)
                    AddRayMarkers(wave, r, c, dir, Vector2.Distance(c, from), Vector2.Distance(c, to));
            }

            if (wave.Markers.Count > 0)
                wave.MarkerNote = "pillars mark the rays";

            wave.Summary = outward ? FormattableString.Invariant($"a starburst of {n} balls from ({c.X:0.#}, {c.Y:0.#})") : FormattableString.Invariant($"{n} balls converging on ({c.X:0.#}, {c.Y:0.#})");
            return wave;
        }

        /// <summary>
        /// Marker points along one ray from <paramref name="c"/> in <paramref name="dir"/>: every
        /// <see cref="HazardDefaults.BallsFanMarkerSpacing"/> m from the centre, kept only inside the rolled stretch
        /// [<paramref name="near"/>, <paramref name="far"/>] (distances from the centre) and on a placeable point.
        /// </summary>
        private static void AddRayMarkers(BallWave wave, HazardRegion r, Vector2 c, Vector2 dir, float near, float far)
        {
            var lo = Math.Min(near, far);
            var hi = Math.Max(near, far);

            for (var m = (float)HazardDefaults.BallsFanMarkerSpacing; m <= hi; m += (float)HazardDefaults.BallsFanMarkerSpacing)
            {
                if (m < lo)
                    continue;

                var pt = c + dir * m;

                if (r.IsPlaceable(pt.X, pt.Y))
                    wave.Markers.Add(pt);
            }
        }

        // ---- rows ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The wall of a row: ball centres along the lateral axis in [lo, hi] with ONE gap centred on
        /// <paramref name="gc"/>. The flank balls sit exactly gap/2 either side of gc, and the wall runs outward from
        /// them at a pitch of 0.9 x 2 x reach (under the 2 x reach a player needs to slip between two balls), the last
        /// ball on the lateral limit so no strip is left open by the wall.
        /// </summary>
        public static List<float> WallPositions(float lo, float hi, float gc, float gap, float reach)
        {
            var pitch = 0.9f * 2f * reach;
            var list = new List<float>();
            var left = Math.Max(lo, gc - gap * 0.5f);
            var right = Math.Min(hi, gc + gap * 0.5f);

            for (var q = left; ; q -= pitch)
            {
                if (q <= lo + 1e-3f)
                {
                    if (list.Count == 0 || list[list.Count - 1] - lo > 0.5f)
                        list.Add(lo);

                    break;
                }

                list.Add(q);
            }

            var rightStart = list.Count;

            for (var q = right; ; q += pitch)
            {
                if (q >= hi - 1e-3f)
                {
                    if (list.Count == rightStart || hi - list[list.Count - 1] > 0.5f)
                        list.Add(hi);

                    break;
                }

                list.Add(q);
            }

            return list;
        }

        /// <summary>
        /// The wall of a row with SEVERAL openings (gaps &gt; 1): ball centres in [lo, hi] with an opening centred on each
        /// of <paramref name="centres"/> (ascending). Each opening's flank balls sit exactly gap/2 either side of its
        /// centre; every stretch of wall (lo to the first opening, between openings, the last opening to hi) is filled
        /// at the same 0.9 x 2 x reach pitch as <see cref="WallPositions"/>, with a ball on each of its ends.
        /// </summary>
        public static List<float> WallPositions(float lo, float hi, IReadOnlyList<float> centres, float gap, float reach)
        {
            var pitch = 0.9f * 2f * reach;
            var list = new List<float>();
            var from = lo;

            for (var i = 0; i <= centres.Count; i++)
            {
                var to = i < centres.Count ? Math.Max(lo, centres[i] - gap * 0.5f) : hi;

                if (to >= from - 1e-3f)
                {
                    for (var q = from; q < to - 1e-3f; q += pitch)
                        list.Add(q);

                    if (list.Count == 0 || to - list[list.Count - 1] > 1e-3f)
                        list.Add(to);
                }

                if (i < centres.Count)
                    from = Math.Min(hi, centres[i] + gap * 0.5f);
            }

            return list;
        }

        /// <summary>
        /// Centres of <paramref name="n"/> openings, each <paramref name="gap"/> wide, along [lo, hi]: random,
        /// ascending and non-overlapping, with at least one ball-width (2 x reach) of wall between neighbours and at both
        /// ends. The slack (span - n x gap - (n + 1) x 2 x reach) is split by n sorted uniform draws, so every fitting
        /// layout can come up. A span too short for that (Validate refuses it) gets the openings spaced evenly.
        /// </summary>
        public static List<float> GapCentres(float lo, float hi, int n, float gap, float reach, System.Random rng)
            => GapCentresWithWall(lo, hi, n, gap, 2f * reach, rng);

        /// <summary><see cref="GapCentres"/> with <paramref name="wall"/> metres of wall at least between neighbours and at both ends (no_overlap passes its pitch).</summary>
        public static List<float> GapCentresWithWall(float lo, float hi, int n, float gap, float wall, System.Random rng)
        {
            var slack = (hi - lo) - n * gap - (n + 1) * wall;
            var centres = new List<float>(n);

            if (slack < 0f)
            {
                for (var i = 0; i < n; i++)
                    centres.Add(lo + (hi - lo) * (i + 1) / (n + 1));

                return centres;
            }

            var draws = new List<float>(n);

            for (var i = 0; i < n; i++)
                draws.Add((float)rng.NextDouble() * slack);

            draws.Sort();

            for (var i = 0; i < n; i++)
                centres.Add(lo + wall + gap * 0.5f + draws[i] + i * (gap + wall));

            return centres;
        }

        private static readonly BallSide[] AllSides = { BallSide.North, BallSide.East, BallSide.South, BallSide.West };

        /// <summary>
        /// A row's side. dir=random draws one of the four (rng.Next(4)); with distinct_dirs, <paramref name="busy"/> (the
        /// walls of the waves in flight) is struck out first and one of the rest is drawn uniformly, in compass order.
        /// No busy wall draws exactly what plain dir=random draws. Every wall busy (the cadence holds such a launch, so
        /// only a caller that skipped the hold gets here) falls back to all four.
        /// </summary>
        private static BallSide RowSide(BallParams p, System.Random rng, IReadOnlyCollection<BallSide> busy)
        {
            if (p.Dir != "random")
                return ParseSide(p.Dir[0]);

            if (busy == null || busy.Count == 0)
                return (BallSide)rng.Next(4);

            return PickFree(rng, AllSides, busy);
        }

        /// <summary>One of <paramref name="walls"/> not in <paramref name="busy"/>, drawn uniformly (one rng.Next); all of them when every one is busy.</summary>
        private static BallSide PickFree(System.Random rng, IReadOnlyList<BallSide> walls, IReadOnlyCollection<BallSide> busy)
        {
            var free = busy == null ? walls.ToList() : walls.Where(s => !busy.Contains(s)).ToList();

            if (free.Count == 0)
                free = walls.ToList();

            return free[rng.Next(free.Count)];
        }

        /// <summary>
        /// The walls the cadence must find free for one more wave of <paramref name="pattern"/> under distinct_dirs, given
        /// the walls <paramref name="busy"/> already in use and <paramref name="pending"/> more waves about to take theirs:
        /// a row needs one of the four walls, a cross one north/south AND one east/west wall.
        /// </summary>
        public static bool WallFree(string pattern, IReadOnlyCollection<BallSide> busy, int pending = 0)
        {
            var used = busy ?? (IReadOnlyCollection<BallSide>)Array.Empty<BallSide>();

            if (pattern == Cross)
            {
                var vertical = used.Count(LateralIsX);
                var horizontal = used.Count(s => !LateralIsX(s));
                return vertical + pending < 2 && horizontal + pending < 2;
            }

            return used.Count + pending < 4;
        }

        private static BallWave PlanRow(HazardRegion r, BallParams p, System.Random rng, IReadOnlyCollection<BallSide> busy)
        {
            var wave = new BallWave();
            var side = RowSide(p, rng, busy);

            if (p.NoOverlap)
                AddRowNoOverlap(r, side, p, rng, wave, markGap: true);
            else if (p.Gaps > 1)
                AddRowOpenings(r, side, p, rng, null, wave, markGap: true);
            else
                AddRow(r, side, p, rng, null, wave, markGap: true);
            wave.MarkerNote = "a pillar marks the way through";

            wave.Summary = p.Gaps > 1
                ? FormattableString.Invariant($"a row from the {Name(side)} with {p.Gaps} openings, rolling {Name(Opposite(side))}")
                : FormattableString.Invariant($"a row from the {Name(side)}, rolling {Name(Opposite(side))}");
            return wave;
        }

        private static readonly BallSide[] VerticalWalls = { BallSide.North, BallSide.South };
        private static readonly BallSide[] HorizontalWalls = { BallSide.East, BallSide.West };

        private static BallWave PlanCross(HazardRegion r, BallParams p, System.Random rng, IReadOnlyCollection<BallSide> busy)
        {
            var wave = new BallWave();
            BallSide vertical, horizontal;

            if (p.Dirs == "random" && busy != null && busy.Count > 0)
            {
                // distinct_dirs: each wall drawn from its pair minus the walls in flight (both free: exactly the draws below)
                vertical = PickFree(rng, VerticalWalls, busy);
                horizontal = PickFree(rng, HorizontalWalls, busy);
            }
            else if (p.Dirs == "random")
            {
                vertical = rng.Next(2) == 0 ? BallSide.North : BallSide.South;
                horizontal = rng.Next(2) == 0 ? BallSide.East : BallSide.West;
            }
            else
            {
                var a = ParseSide(p.Dirs[0]);
                var b = ParseSide(p.Dirs[1]);
                vertical = LateralIsX(a) ? a : b;
                horizontal = LateralIsX(a) ? b : a;
            }

            // The vertical row's gap is a lane at x = gx, the horizontal row's a lane at y = gy: the safe spot is (gx, gy).
            var (xLo, xHi) = LateralRange(r, vertical, p);
            var (yLo, yHi) = LateralRange(r, horizontal, p);

            if (p.NoOverlap)
                return PlanCrossNoOverlap(r, p, rng, vertical, horizontal, xLo, xHi, yLo, yHi);

            if (p.Gaps > 1)
                return PlanCrossOpenings(r, p, rng, vertical, horizontal, xLo, xHi, yLo, yHi);

            var gx = (xLo + xHi) * 0.5f;
            var gy = (yLo + yHi) * 0.5f;
            var found = false;

            for (var attempt = 0; attempt < 32 && !found; attempt++)
            {
                var cx = xLo + p.Gap * 0.5f + (float)rng.NextDouble() * Math.Max(0f, xHi - xLo - p.Gap);
                var cy = yLo + p.Gap * 0.5f + (float)rng.NextDouble() * Math.Max(0f, yHi - yLo - p.Gap);

                if (SafeSpotOk(r, cx, cy, p))
                {
                    gx = cx;
                    gy = cy;
                    found = true;
                }
            }

            AddRow(r, vertical, p, rng, gx, wave, markGap: false);
            AddRow(r, horizontal, p, rng, gy, wave, markGap: false);

            // ONE pillar: the intersection, the only spot safe from both rows
            wave.SafePoint = new Vector2(gx, gy);
            wave.Markers.Add(wave.SafePoint.Value);
            wave.MarkerNote = "a pillar marks the safe spot where the gaps cross";
            wave.Summary = FormattableString.Invariant($"a cross: rows from the {Name(vertical)} and the {Name(horizontal)}, safe where the gaps meet");
            return wave;
        }

        /// <summary>
        /// A cross with several openings per row (gaps &gt; 1): each row gets its own <see cref="GapCentres"/>, and every
        /// crossing of an x opening with a y opening is safe from both rows. A draw is kept once at least one crossing is
        /// somewhere a player can stand (<see cref="SafeSpotOk"/>; up to 32 draws, else the last). SafePoint is the first
        /// standable crossing; the markers (marker=1) stand on every standable crossing.
        /// </summary>
        private static BallWave PlanCrossOpenings(HazardRegion r, BallParams p, System.Random rng, BallSide vertical, BallSide horizontal, float xLo, float xHi, float yLo, float yHi)
        {
            var wave = new BallWave();
            List<float> xs = null, ys = null;
            List<Vector2> safe = null;

            for (var attempt = 0; attempt < 32; attempt++)
            {
                xs = GapCentres(xLo, xHi, p.Gaps, p.Gap, p.Reach, rng);
                ys = GapCentres(yLo, yHi, p.Gaps, p.Gap, p.Reach, rng);
                safe = xs.SelectMany(x => ys.Select(y => new Vector2(x, y))).Where(c => SafeSpotOk(r, c.X, c.Y, p)).ToList();

                if (safe.Count > 0)
                    break;
            }

            AddRowOpenings(r, vertical, p, rng, xs, wave, markGap: false);
            AddRowOpenings(r, horizontal, p, rng, ys, wave, markGap: false);

            wave.SafePoint = safe.Count > 0 ? safe[0] : new Vector2(xs[0], ys[0]);
            wave.Markers.AddRange(safe.Count > 0 ? safe : new List<Vector2> { wave.SafePoint.Value });
            wave.MarkerNote = "pillars mark the safe spots where the gaps cross";
            wave.Summary = FormattableString.Invariant($"a cross: rows from the {Name(vertical)} and the {Name(horizontal)}, {p.Gaps} openings each, safe where the gaps meet");
            return wave;
        }

        /// <summary>
        /// A cross with no_overlap=1 (any number of openings): each wall's openings are drawn by <see cref="GapCentres"/>
        /// with one no-overlap pitch of wall between and at the ends, then laid by <see cref="NoOverlapWall"/>, which may
        /// shift and widen them; the safe spots are the crossings of the openings AS LAID. A draw is kept once one crossing
        /// is standable (<see cref="SafeSpotOk"/>; up to 32 draws, else the last), as with <see cref="PlanCrossOpenings"/>.
        /// </summary>
        private static BallWave PlanCrossNoOverlap(HazardRegion r, BallParams p, System.Random rng, BallSide vertical, BallSide horizontal, float xLo, float xHi, float yLo, float yHi)
        {
            var wave = new BallWave();
            var wall = BallRules.NoOverlapPitch(p.Reach);
            (List<float> Positions, List<float> Openings) xWall = default, yWall = default;
            List<Vector2> safe = null;

            for (var attempt = 0; attempt < 32; attempt++)
            {
                xWall = NoOverlapWall(xLo, xHi, GapCentresWithWall(xLo, xHi, p.Gaps, p.Gap, wall, rng), p.Gap, p.Reach, rng);
                yWall = NoOverlapWall(yLo, yHi, GapCentresWithWall(yLo, yHi, p.Gaps, p.Gap, wall, rng), p.Gap, p.Reach, rng);
                safe = xWall.Openings.SelectMany(x => yWall.Openings.Select(y => new Vector2(x, y))).Where(c => SafeSpotOk(r, c.X, c.Y, p)).ToList();

                if (safe.Count > 0)
                    break;
            }

            AddWall(r, vertical, p, xWall.Positions, xWall.Openings, wave, markGap: false);
            AddWall(r, horizontal, p, yWall.Positions, yWall.Openings, wave, markGap: false);

            wave.SafePoint = safe.Count > 0 ? safe[0] : new Vector2(xWall.Openings[0], yWall.Openings[0]);
            wave.Markers.AddRange(safe.Count > 0 ? safe : new List<Vector2> { wave.SafePoint.Value });

            if (p.Gaps > 1)
            {
                wave.MarkerNote = "pillars mark the safe spots where the gaps cross";
                wave.Summary = FormattableString.Invariant($"a cross: rows from the {Name(vertical)} and the {Name(horizontal)}, {p.Gaps} openings each, safe where the gaps meet");
            }
            else
            {
                wave.MarkerNote = "a pillar marks the safe spot where the gaps cross";
                wave.Summary = FormattableString.Invariant($"a cross: rows from the {Name(vertical)} and the {Name(horizontal)}, safe where the gaps meet");
            }

            return wave;
        }

        /// <summary>A row with no_overlap=1 (any number of openings): <see cref="GapCentres"/> with a no-overlap pitch of wall, laid by <see cref="NoOverlapWall"/>.</summary>
        private static void AddRowNoOverlap(HazardRegion r, BallSide side, BallParams p, System.Random rng, BallWave wave, bool markGap)
        {
            var (lo, hi) = LateralRange(r, side, p);
            var laid = NoOverlapWall(lo, hi, GapCentresWithWall(lo, hi, p.Gaps, p.Gap, BallRules.NoOverlapPitch(p.Reach), rng), p.Gap, p.Reach, rng);

            AddWall(r, side, p, laid.Positions, laid.Openings, wave, markGap);
        }

        /// <summary>One wall's balls at <paramref name="positions"/> and its openings at <paramref name="openings"/> (lateral coordinates).</summary>
        private static void AddWall(HazardRegion r, BallSide side, BallParams p, IReadOnlyList<float> positions, IReadOnlyList<float> openings, BallWave wave, bool markGap)
        {
            foreach (var q in positions)
            {
                if (TryLane(r, side, q, p, out var start, out var end))
                    wave.Routes.Add(new BallRoute(start, end, 0.0, "row-" + Name(side)));
            }

            foreach (var gc in openings)
            {
                wave.Gaps.Add((side, gc));

                if (markGap && TryLane(r, side, gc, p, out var gapStart, out _))
                    wave.Markers.Add(gapStart);
            }
        }

        /// <summary><paramref name="n"/> opening centres spaced evenly along [lo, hi] (no draw): what Validate counts a wall's balls on.</summary>
        public static List<float> EvenCentres(float lo, float hi, int n)
        {
            var centres = new List<float>(n);

            for (var i = 0; i < n; i++)
                centres.Add(lo + (hi - lo) * (i + 1) / (n + 1));

            return centres;
        }

        /// <summary>
        /// THE NO-OVERLAP WALL (no_overlap=1): ball centres along [lo, hi] at a pitch P = 2 x reach +
        /// <see cref="HazardDefaults.BallsNoOverlapMargin"/>, with an opening near each of <paramref name="centres"/>
        /// (ascending). Every stretch of wall (lo to the first opening, between openings, the last opening to hi) is a
        /// whole number k &gt;= 1 of pitches, a ball on each end of it. The whole wall takes K = floor((span - n x gap) / P)
        /// pitches in all, shared out over the stretches in proportion to their drawn lengths (largest remainder, at least
        /// one each), and the span left over (under one pitch) widens the openings: split at random across them when
        /// <paramref name="rng"/> is given (n - 1 sorted uniform cuts, so the openings move from wave to wave even on a
        /// wall whose stretches have no choice), evenly without it (Validate's ball count). So the openings can move off
        /// the drawn centres by up to about one pitch, are never narrower than gap, and no two neighbouring balls are ever
        /// closer than P. Returns the positions (ascending, the last on hi) and the openings' centres as laid.
        ///
        /// THE 1 CM STRIP IS INTENDED. P is 2 x reach + 0.02, so midway between two wall balls a standing player is
        /// reach + 0.01 from each and is hit by neither (the hit test is distance &lt;= reach). That is the owner's rule
        /// ("a standing player never takes two hits"): any pitch over 2 x reach leaves such a strip, and a pitch under it
        /// lets two neighbouring balls both hit one standing player. Do not "fix" it by shrinking the pitch.
        /// Validate guarantees n x gap + (n + 1) x P &lt;= span; a span short of that still gets n + 1 one-pitch stretches,
        /// with the openings narrowed to fit.
        /// </summary>
        public static (List<float> Positions, List<float> Openings) NoOverlapWall(float lo, float hi, IReadOnlyList<float> centres, float gap, float reach, System.Random rng = null)
        {
            var pitch = (double)BallRules.NoOverlapPitch(reach);
            var n = centres.Count;
            var wallSpan = (double)(hi - lo) - n * (double)gap;
            var total = Math.Max(n + 1, (int)Math.Floor(wallSpan / pitch + 1e-6));

            // the drawn stretch lengths, in pitches
            var want = new double[n + 1];
            var prev = (double)lo;

            for (var i = 0; i < n; i++)
            {
                want[i] = Math.Max(0.0, centres[i] - gap * 0.5 - prev) / pitch;
                prev = centres[i] + gap * 0.5;
            }

            want[n] = Math.Max(0.0, hi - prev) / pitch;

            var k = new int[n + 1];

            for (var i = 0; i <= n; i++)
                k[i] = Math.Max(1, (int)Math.Floor(want[i] + 1e-6));

            // largest remainder: take from the most over-served stretch, give to the most under-served one
            while (k.Sum() > total)
            {
                var pick = -1;

                for (var i = 0; i <= n; i++)
                    if (k[i] > 1 && (pick < 0 || want[i] - k[i] < want[pick] - k[pick]))
                        pick = i;

                if (pick < 0)
                    break;

                k[pick]--;
            }

            while (k.Sum() < total)
            {
                var pick = 0;

                for (var i = 1; i <= n; i++)
                    if (want[i] - k[i] > want[pick] - k[pick])
                        pick = i;

                k[pick]++;
            }

            // the leftover widens the openings: a random split (n - 1 sorted uniform cuts) when drawing, else even
            var leftover = wallSpan - k.Sum() * pitch;
            var widen = new double[n];

            if (rng != null && n > 1)
            {
                var cuts = new List<double>(n + 1) { 0.0 };

                for (var i = 0; i < n - 1; i++)
                    cuts.Add(rng.NextDouble() * leftover);

                cuts.Add(leftover);
                cuts.Sort();

                for (var i = 0; i < n; i++)
                    widen[i] = cuts[i + 1] - cuts[i];
            }
            else
            {
                for (var i = 0; i < n; i++)
                    widen[i] = n > 0 ? leftover / n : 0.0;
            }

            var positions = new List<float>();
            var openings = new List<float>(n);
            var x = (double)lo;

            for (var i = 0; i <= n; i++)
            {
                for (var j = 0; j <= k[i]; j++)
                    positions.Add((float)(x + j * pitch));

                x += k[i] * pitch;

                if (i < n)
                {
                    openings.Add((float)(x + (gap + widen[i]) * 0.5));
                    x += gap + widen[i];
                }
            }

            // the last ball sits on the lateral limit (the sums above land there to rounding)
            positions[positions.Count - 1] = hi;

            return (positions, openings);
        }

        /// <summary>The cross intersection must be somewhere a player can stand: in the shape (inset on a circle) and outside every exclusion.</summary>
        private static bool SafeSpotOk(HazardRegion r, float x, float y, BallParams p)
            => r.IsPlaceable(x, y) && (r.Shape != HazardShape.Circle || r.DistanceToEdge(x, y) >= p.Inset - 1e-3f);

        /// <summary>A row with several openings (gaps &gt; 1): <paramref name="forced"/> centres (a cross), else <see cref="GapCentres"/>.</summary>
        private static void AddRowOpenings(HazardRegion r, BallSide side, BallParams p, System.Random rng, IReadOnlyList<float> forced, BallWave wave, bool markGap)
        {
            var (lo, hi) = LateralRange(r, side, p);
            var centres = forced ?? GapCentres(lo, hi, p.Gaps, p.Gap, p.Reach, rng);

            foreach (var q in WallPositions(lo, hi, centres, p.Gap, p.Reach))
            {
                if (TryLane(r, side, q, p, out var start, out var end))
                    wave.Routes.Add(new BallRoute(start, end, 0.0, "row-" + Name(side)));
            }

            foreach (var gc in centres)
            {
                wave.Gaps.Add((side, gc));

                if (markGap && TryLane(r, side, gc, p, out var gapStart, out _))
                    wave.Markers.Add(gapStart);
            }
        }

        private static void AddRow(HazardRegion r, BallSide side, BallParams p, System.Random rng, float? forcedGap, BallWave wave, bool markGap)
        {
            var (lo, hi) = LateralRange(r, side, p);
            var span = hi - lo;
            var gc = forcedGap ?? (span >= p.Gap ? lo + p.Gap * 0.5f + (float)rng.NextDouble() * (span - p.Gap) : (lo + hi) * 0.5f);

            foreach (var q in WallPositions(lo, hi, gc, p.Gap, p.Reach))
            {
                if (TryLane(r, side, q, p, out var start, out var end))
                    wave.Routes.Add(new BallRoute(start, end, 0.0, "row-" + Name(side)));
            }

            wave.Gaps.Add((side, gc));

            if (markGap && TryLane(r, side, gc, p, out var gapStart, out _))
                wave.Markers.Add(gapStart);
        }

        // ---- random ----------------------------------------------------------------------------------------------

        // ---- fan -------------------------------------------------------------------------------------------------

        /// <summary>The fan's origin: cx/cy, else the region centre.</summary>
        public static Vector2 FanOrigin(HazardRegion r, BallParams p) => p.Centre ?? r.Center;

        /// <summary>
        /// The ray angles (radians) of a fan of <paramref name="count"/> balls centred on <paramref name="aim"/>:
        /// the middle ball (or the middle of an even count) on the aim, the rest evenly across
        /// <paramref name="spreadDegrees"/>: aim + spread x (i / (count - 1) - 1/2). Count 1: just the aim. Count 3:
        /// aim - spread/2, aim, aim + spread/2.
        /// </summary>
        public static double[] FanAngles(double aim, int count, double spreadDegrees)
        {
            var n = Math.Max(1, count);
            var spread = Math.Clamp(spreadDegrees, 0.0, HazardDefaults.BallsFanMaxSpread) * Math.PI / 180.0;
            var angles = new double[n];

            for (var i = 0; i < n; i++)
                angles[i] = n == 1 ? aim : aim + spread * ((double)i / (n - 1) - 0.5);

            return angles;
        }

        /// <summary>
        /// One fan volley at <paramref name="target"/>. The aim is FIXED AT WAVE START from where the target stands then,
        /// <c>lead</c> seconds before the balls launch; balls never home, so a target that moves during the lead sidesteps
        /// the line it was shown. Each ball rolls from the origin straight to the region edge pulled in by edge_inset.
        /// The marker pillars lie along the aim line, one every <see cref="HazardDefaults.BallsFanMarkerSpacing"/> m from
        /// the origin, to telegraph the line of fire.
        /// </summary>
        public static BallWave PlanFan(HazardRegion r, BallParams p, Vector2 target, Vector2? targetFacing = null)
            => PlanFanAlong(r, p, FanAim(FanOrigin(r, p), target, targetFacing, r.Center, p.Reach).Angle);

        /// <summary>
        /// THE FAN'S AIM (radians, +x = 0, +y = pi/2) and where it came from. Normally straight at
        /// <paramref name="target"/>. A DEGENERATE aim - the target stands within <paramref name="minDistance"/> (one
        /// reach) of the origin, e.g. /hazard ... at=me target=nearest, where the origin IS the caller - has no
        /// direction worth trusting (a few centimetres of drift would pick it), so it falls back, in order, to: the
        /// target's FACING when it is finite and non-zero (the solo tester points the fan by facing; a boss's target
        /// under its head is hit along where it looks), the direction to the region centre (from an origin
        /// off-centre), and +x. Never a zero vector, so the balls always roll out to the region edge.
        /// </summary>
        public static FanAimResult FanAim(Vector2 origin, Vector2 target, Vector2? targetFacing, Vector2 regionCentre, float minDistance)
        {
            var min = Math.Max(1e-2f, minDistance);
            var d = target - origin;

            if (d.LengthSquared() >= min * min)
                return new FanAimResult(Math.Atan2(d.Y, d.X), FanAimSource.Target);

            if (targetFacing is Vector2 f && float.IsFinite(f.X) && float.IsFinite(f.Y) && f.LengthSquared() > 1e-6f)
                return new FanAimResult(Math.Atan2(f.Y, f.X), FanAimSource.Facing);

            var c = regionCentre - origin;

            if (c.LengthSquared() >= min * min)
                return new FanAimResult(Math.Atan2(c.Y, c.X), FanAimSource.RegionCentre);

            return new FanAimResult(0.0, FanAimSource.PlusX);
        }
        /// <summary>One fan volley along the fixed <paramref name="aim"/> (radians). See <see cref="PlanFan"/>.</summary>
        public static BallWave PlanFanAlong(HazardRegion r, BallParams p, double aim)
        {
            var wave = new BallWave();
            var o = FanOrigin(r, p);

            foreach (var angle in FanAngles(aim, p.Count, p.Spread))
            {
                var dir = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                var exit = RayExit(r, o, dir, p.Inset);

                // origin_clear (opt-in): the ball starts that far out along its ray, never on the origin itself
                if (p.OriginClear > 0f && p.OriginClear >= exit)
                    continue;

                var from = p.OriginClear > 0f ? o + dir * p.OriginClear : o;
                var to = o + dir * exit;

                if (Trim(r, ref from, ref to))
                    wave.Routes.Add(new BallRoute(from, to, 0.0, "fan"));
            }

            var mid = new Vector2((float)Math.Cos(aim), (float)Math.Sin(aim));
            var reach = RayExit(r, o, mid, p.Inset);

            for (var m = (float)HazardDefaults.BallsFanMarkerSpacing; m <= reach; m += (float)HazardDefaults.BallsFanMarkerSpacing)
            {
                // with origin_clear, no pillar inside the clearance: no ball rolls there
                if (m < p.OriginClear)
                    continue;

                var pt = o + mid * m;

                if (r.IsPlaceable(pt.X, pt.Y))
                    wave.Markers.Add(pt);
            }

            wave.MarkerNote = "pillars mark the line of fire";
            wave.Summary = FormattableString.Invariant($"an aimed fan of {p.Count} balls ({p.Spread:0.#} degrees) from ({o.X:0.#}, {o.Y:0.#})");
            return wave;
        }

        /// <summary>
        /// Which of <paramref name="players"/> (landblock-local x/y) the fan aims at: "nearest" is the one closest to
        /// <paramref name="origin"/>, otherwise a random one drawn from <paramref name="rng"/>. -1 when there is none.
        /// </summary>
        public static int PickTarget(IReadOnlyList<Vector2> players, Vector2 origin, string mode, System.Random rng)
        {
            if (players == null || players.Count == 0)
                return -1;

            if (mode == "nearest")
            {
                var best = 0;

                for (var i = 1; i < players.Count; i++)
                    if (Vector2.DistanceSquared(players[i], origin) < Vector2.DistanceSquared(players[best], origin))
                        best = i;

                return best;
            }

            return rng.Next(players.Count);
        }

        private static BallWave PlanRandom(HazardRegion r, BallParams p, System.Random rng)
        {
            var wave = new BallWave();
            var slot = p.Window / Math.Max(1, p.Count);

            for (var i = 0; i < p.Count; i++)
            {
                var side = (BallSide)rng.Next(4);
                var (lo, hi) = LateralRange(r, side, p);
                var lat = lo + (float)rng.NextDouble() * (hi - lo);
                var delay = (i + 0.1 + 0.8 * rng.NextDouble()) * slot;

                if (TryLane(r, side, lat, p, out var start, out var end))
                    wave.Routes.Add(new BallRoute(start, end, delay, "random-" + Name(side)));
            }

            // A STREAM ramps speed LAUNCH by launch: route k of n (delay order; the draws above are unchanged) gets its own
            // speed. Without a stream the ramp is across the waves (BallRules.ForWave), so every ball keeps the wave's speed.
            if (p.Stream > 0.0 && p.SpeedEnd.HasValue)
            {
                var n = wave.Routes.Count;

                for (var k = 0; k < n; k++)
                {
                    var route = wave.Routes[k];
                    wave.Routes[k] = new BallRoute(route.From, route.To, route.Delay, route.Tag, (float)HazardRamp.At(p.Speed, p.SpeedEnd, k + 1, n));
                }
            }

            wave.Summary = FormattableString.Invariant($"{p.Count} balls launched at random over {p.Window:0.#} s");
            return wave;
        }
    }
}
