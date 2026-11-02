using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>A side of a region. N is +y, E is +x (the same compass /hazard move prints).</summary>
    public enum WispCompass
    {
        N,
        E,
        S,
        W,
    }

    /// <summary>The order the wisps of one formation burst in.</summary>
    public enum WispFire
    {
        /// <summary>All at once, <c>hold</c> seconds after the formation is reached.</summary>
        Unison,

        /// <summary>Each at <c>hold</c> + a random extra of 0..hold seconds.</summary>
        Random,

        /// <summary>In formation order, <c>seq_gap</c> seconds apart, the first <c>hold</c> seconds after arrival.</summary>
        Sequence,
    }

    /// <summary>
    /// The wisps mechanic's numbers, read from the merged <see cref="HazardArgs"/> (shared invariant 5: never from
    /// PropertyManager), each with a documented default and clamp. Pure.
    ///
    ///   key           default  clamp      meaning
    ///   n             8        1..32      wisps in a formation (fewer when the pattern cannot fit them with gaps)
    ///   scale         1.0      0.25..4    wisp model scale
    ///   move          2.5      0.5..30    seconds the wisps glide to a formation (all of them, in unison)
    ///   hold          1.5      0.2..60    THE TELEGRAPH, the reaction time players get: seconds the wisps sit at their spots before bursting
    ///                                     (Validate: at least 0.5 + (pillar_radius + player_radius) / escape_speed)
    ///   escape_speed  3.0      1..12      m/s a player is assumed to run out of a pillar (the same key and meaning as the balls mechanic)
    ///   burst         1.5      0.2..30    seconds a pillar burns (and damages, once per player per pillar)
    ///   strike_delay  0.2      0..1       seconds a pillar is on screen before it can damage, so the client draws it before the hit
    ///                                     lands (at most half of burst; StrikeDelaySeconds). Without it a lethal hit could arrive
    ///                                     with the pillar's CreateObject and the player died without ever seeing it
    ///   rest          1.0      0..60      seconds between cycles
    ///   move_end      (none)   0.5..30    ramp: the last cycle's move; cycle 1 uses move, the last uses move_end, linear between (HazardRamp.At)
    ///   hold_end      (none)   0.2..60    ramp: the last cycle's hold (Validate applies the hold floor to the lesser of hold and hold_end)
    ///   rest_end      (none)   0..60      ramp: the last cycle's rest
    ///   hold_floor    1        0|1        0 lifts the hold floor above for BOTH hold and hold_end (hold keeps its 0.2 s clamp); the safe default is 1
    ///   follow        0        0|1        one extra follower wisp trails the first player in the region (WispFollowRules); not counted in n or cycles
    ///   mark_dest     0        0|1        1 marks each wisp's destination (its Pillar point) on the ground from the glide's start until its burst, so the move is warning time too
    ///   ceiling_beam  0        0..5       seconds before each burst that a narrow beam falls from the ceiling onto the wisp's Pillar point (0 = off; visual only, never damages)
    ///   beam_script   (beam)   script id  decimal or 0x hex: the look of the ceiling beam, default HazardDefaults.BeamScript
    ///   follow_lag    2.0      0.5..10    seconds the follower trails its target by; it bursts on the crew's beat (WispDirector), so there is no cadence key
    ///   pillar_radius 2.0      0.5..15    2D DAMAGE radius of a pillar (the player's is added). NOT the visual size: the flames are fixed by the effect script
    ///   cycles        8        1..500     formations (or sweeps) per run
    ///   fire          unison   unison|random|sequence
    ///   seq_gap       0.25     0..5       seconds between wisps for fire=sequence
    ///   dir           random   n|s|e|w|random   the side a pattern is measured from: row n|s (it runs west-east), column e|w (it runs north-south), sweep any; random picks within the pattern's axis
    ///   sweep_move    1.0      0.3..30    glide time between the steps of a sweep (the first step uses move)
    ///   steps         0 (auto) 0..40      sweep steps; auto = enough to tile the region depth with pillar_radius bands
    ///   ring_radius   half the inscribed radius   0.5..200
    ///   cx, cy        the region centre   centre of ring and diagonal (must be inside the region)
    ///   scatter_cover 0.5      0.1..0.8   scatter: at most this fraction of the usable area is covered by pillars
    ///   wisp_clear    max(0.5, 0.48 x scale)  0..3  how far a blocked wisp stands back from a solid
    ///   wisp_wcid     1006950               the wisp weenie
    ///   script        0x33001028            the pillar effect
    ///   player_radius 0.5      0..3       (framework key) the player's radius
    ///   edge_inset    2.0      0..10      (framework key) formations stay this far inside the region edge
    ///   pct           0.60     0..5       (framework key) damage as a fraction of max health
    /// </summary>
    public sealed class WispSettings
    {
        public const int DefaultCount = 8;
        public const double DefaultMove = 2.5;
        public const double DefaultHold = 1.5;
        public const double DefaultBurst = 1.5;
        public const double DefaultRest = 1.0;
        public const double DefaultStrikeDelay = 0.2;
        public const double DefaultPillarRadius = 2.0;
        public const int DefaultCycles = 8;
        public const double DefaultScatterCover = 0.5;

        /// <summary>The shared escape_speed assumption (<see cref="HazardDefaults.EscapeSpeed"/>), the very constant the balls use.</summary>
        public const double DefaultEscapeSpeed = HazardDefaults.EscapeSpeed;

        /// <summary>The longest a run may take: cycles x steps x (move + hold + burst + rest). Validate refuses more.</summary>
        public const double MaxRunSeconds = 600.0;

        public int Count { get; private set; }

        /// <summary>The fewest and most wisps a squad may have (the "n" arg's own range, also the clamp of HazardRun.LiveCount).</summary>
        public const int MinCount = 1;

        public const int MaxCount = 32;

        /// <summary>These settings with the squad size <paramref name="count"/> (this very object when it already is that). Used for the formation plan of a cycle whose live count differs from n.</summary>
        internal WispSettings WithCount(int count)
        {
            if (count == Count)
                return this;

            var copy = (WispSettings)MemberwiseClone();
            copy.Count = count;
            return copy;
        }
        public double EscapeSpeed { get; private init; }
        public float Scale { get; private init; }
        public double Move { get; private init; }
        public double Hold { get; private init; }
        public double Burst { get; private init; }
        public double Rest { get; private init; }

        /// <summary>args "strike_delay" (clamped 0..1): see <see cref="StrikeDelaySeconds"/>.</summary>
        public double StrikeDelay { get; private init; }

        /// <summary>
        /// How long after a pillar appears it may first damage: strike_delay, but never more than half of burst, so the
        /// first strike's scheduled step is always due before the host's destroy step (WispDirector.Pillar).
        /// </summary>
        public double StrikeDelaySeconds => Math.Min(StrikeDelay, Burst * 0.5);

        /// <summary>args "move_end": the last cycle's move (clamped like "move"). Null: no ramp.</summary>
        public double? MoveEnd { get; private init; }

        /// <summary>args "hold_end": the last cycle's hold (clamped like "hold"). Null: no ramp.</summary>
        public double? HoldEnd { get; private init; }

        /// <summary>args "rest_end": the last cycle's rest (clamped like "rest"). Null: no ramp.</summary>
        public double? RestEnd { get; private init; }

        /// <summary>args "hold_floor", default true. False lets Validate accept a hold (or hold_end) under <see cref="EscapeHoldFloor"/>: an owner ruling for a boss phase that tightens the telegraph on purpose.</summary>
        public bool HoldFloor { get; private init; }

        /// <summary>args "follow", default false: the run adds one follower wisp (<see cref="WispFollowRules"/>).</summary>
        public bool Follow { get; private init; }

        /// <summary>args "mark_dest", default false: each glide marks every wisp's destination on the ground until its burst (WispDirector).</summary>
        public bool MarkDest { get; private init; }

        /// <summary>args "ceiling_beam", seconds, default 0 (off), 0..5 (Validate refuses outside it): a beam falls from the ceiling onto each Pillar point for this long before its burst (WispDirector).</summary>
        public double CeilingBeam { get; private init; }

        /// <summary>args "beam_script" (decimal or 0x hex), default <see cref="HazardDefaults.BeamScript"/>: the effect script of the ceiling beam.</summary>
        public uint BeamScript { get; private init; }

        /// <summary>args "follow_lag", seconds, default 2, clamped 0.5..10: how far behind its target the follower is.</summary>
        public double FollowLag { get; private init; }

        public float PillarRadius { get; private init; }
        public int Cycles { get; private init; }
        public WispFire Fire { get; private init; }
        public double SeqGap { get; private init; }
        public string Dir { get; private init; }
        public double SweepMove { get; private init; }
        public int Steps { get; private init; }
        public float? RingRadius { get; private init; }
        public float? CentreX { get; private init; }
        public float? CentreY { get; private init; }
        public double ScatterCover { get; private init; }
        public float WispClear { get; private init; }
        public uint WispWcid { get; private init; }
        public uint Script { get; private init; }

        /// <summary>args "mark_script" (decimal or 0x hex), default <see cref="HazardDefaults.WispDestMarkScript"/>: the effect script of the mark_dest ground mark (an element-coloured marker).</summary>
        public uint MarkScript { get; private init; }
        public float PlayerRadius { get; private init; }
        public float EdgeInset { get; private init; }

        /// <summary>The 2D radius every pillar of every pattern is hit-tested with (HazardContext.HitPlayers adds the player's).</summary>
        public float HitRadius => PillarRadius;

        /// <summary>The least hold a run may give: 0.5 s plus the time to run out of a pillar (pillar_radius + player_radius) at escape_speed.</summary>
        public double EscapeHoldFloor => 0.5 + ((double)PillarRadius + PlayerRadius) / EscapeSpeed;

        /// <summary>The hit window every pillar of every pattern uses: one hit per player per pillar host (+infinity, the same window the balls use; every burst gets a NEW host, so a player is hit once per burst).</summary>
        public double HitWindow => double.PositiveInfinity;

        /// <summary>
        /// The corridor a player needs between two pillar discs: 2 x player_radius + 0.5 m. Every line, row, sweep
        /// and ring leaves at least this between adjacent discs; scatter between ANY two.
        /// </summary>
        public float MinGap => 2f * PlayerRadius + 0.5f;

        /// <summary>The least distance between two wisp centres that still leaves <see cref="MinGap"/> between their discs: 2 x pillar_radius + MinGap.</summary>
        public float MinSpacing => 2f * PillarRadius + MinGap;

        public static WispSettings From(HazardArgs a)
        {
            a ??= HazardArgs.Empty;

            var scale = (float)a.GetDouble("scale", 1.0, 0.25, 4.0);

            return new WispSettings
            {
                Count = a.GetInt("n", DefaultCount, WispSettings.MinCount, WispSettings.MaxCount),
                EscapeSpeed = a.GetDouble(HazardDefaults.EscapeSpeedKey, DefaultEscapeSpeed, HazardDefaults.MinEscapeSpeed, HazardDefaults.MaxEscapeSpeed),
                Scale = scale,
                Move = a.GetDouble("move", DefaultMove, 0.5, 30.0),
                Hold = a.GetDouble("hold", DefaultHold, 0.2, 60.0),
                Burst = a.GetDouble("burst", DefaultBurst, 0.2, 30.0),
                StrikeDelay = a.GetDouble("strike_delay", DefaultStrikeDelay, 0.0, 1.0),
                Rest = a.GetDouble("rest", DefaultRest, 0.0, 60.0),
                MoveEnd = a.Has("move_end") ? a.GetDouble("move_end", DefaultMove, 0.5, 30.0) : (double?)null,
                HoldEnd = a.Has("hold_end") ? a.GetDouble("hold_end", DefaultHold, 0.2, 60.0) : (double?)null,
                RestEnd = a.Has("rest_end") ? a.GetDouble("rest_end", DefaultRest, 0.0, 60.0) : (double?)null,
                HoldFloor = a.GetBool("hold_floor", true),
                Follow = a.GetBool("follow", false),
                MarkDest = a.GetBool("mark_dest", false),
                CeilingBeam = a.GetDouble("ceiling_beam", 0.0, 0.0, 5.0),
                BeamScript = HazardToolRoutes.ParseScript(a.GetString("beam_script"), HazardDefaults.BeamScript),
                FollowLag = a.GetDouble("follow_lag", WispFollowRules.DefaultLag, 0.5, 10.0),
                PillarRadius = (float)a.GetDouble("pillar_radius", DefaultPillarRadius, 0.5, 15.0),
                Cycles = a.GetInt("cycles", DefaultCycles, 1, 500),
                Fire = ParseFire(a.GetChoice("fire", "unison", "unison", "random", "sequence")),
                SeqGap = a.GetDouble("seq_gap", 0.25, 0.0, 5.0),
                Dir = a.GetChoice("dir", "random", "n", "s", "e", "w", "random"),
                SweepMove = a.GetDouble("sweep_move", 1.0, 0.3, 30.0),
                Steps = a.GetInt("steps", 0, 0, 40),
                RingRadius = a.Has("ring_radius") ? (float)a.GetDouble("ring_radius", 1.0, 0.5, 200.0) : (float?)null,
                CentreX = a.Has("cx") ? (float)a.GetDouble("cx", 0.0, -1e6, 1e6) : (float?)null,
                CentreY = a.Has("cy") ? (float)a.GetDouble("cy", 0.0, -1e6, 1e6) : (float?)null,
                ScatterCover = a.GetDouble("scatter_cover", DefaultScatterCover, 0.1, 0.8),
                WispClear = (float)a.GetDouble("wisp_clear", Math.Max(0.5, HazardDefaults.WispRadiusAtScale1 * scale), 0.0, 3.0),
                WispWcid = (uint)a.GetInt("wisp_wcid", (int)HazardDefaults.EmberWispWcid, 1, int.MaxValue),
                Script = HazardToolRoutes.ParseScript(a.GetString("script"), HazardDefaults.PillarScript),
                MarkScript = HazardToolRoutes.ParseScript(a.GetString("mark_script"), HazardDefaults.WispDestMarkScript),
                PlayerRadius = (float)a.GetDouble(HazardDefaults.PlayerRadiusKey, HazardDefaults.PlayerRadius, HazardDefaults.MinPlayerRadius, HazardDefaults.MaxPlayerRadius),
                EdgeInset = (float)a.GetDouble(HazardDefaults.EdgeInsetKey, HazardDefaults.EdgeInset, HazardDefaults.MinEdgeInset, HazardDefaults.MaxEdgeInset),
            };
        }

        /// <summary>
        /// The numbers cycle <paramref name="cycle"/> (1-based) runs with: a copy of these settings whose move, hold and
        /// rest are ramped over <see cref="Cycles"/> by <see cref="HazardRamp.At"/> towards move_end, hold_end and
        /// rest_end. With no end key it is these settings themselves, so a run without ramps is exactly as before.
        /// </summary>
        public WispSettings ForCycle(int cycle)
        {
            if (MoveEnd == null && HoldEnd == null && RestEnd == null)
                return this;

            return new WispSettings
            {
                Count = Count,
                EscapeSpeed = EscapeSpeed,
                Scale = Scale,
                Move = HazardRamp.At(Move, MoveEnd, cycle, Cycles),
                Hold = HazardRamp.At(Hold, HoldEnd, cycle, Cycles),
                Burst = Burst,
                StrikeDelay = StrikeDelay,
                Rest = HazardRamp.At(Rest, RestEnd, cycle, Cycles),
                MoveEnd = MoveEnd,
                HoldEnd = HoldEnd,
                RestEnd = RestEnd,
                HoldFloor = HoldFloor,
                Follow = Follow,
                MarkDest = MarkDest,
                CeilingBeam = CeilingBeam,
                BeamScript = BeamScript,
                FollowLag = FollowLag,
                PillarRadius = PillarRadius,
                Cycles = Cycles,
                Fire = Fire,
                SeqGap = SeqGap,
                Dir = Dir,
                SweepMove = SweepMove,
                Steps = Steps,
                RingRadius = RingRadius,
                CentreX = CentreX,
                CentreY = CentreY,
                ScatterCover = ScatterCover,
                WispClear = WispClear,
                WispWcid = WispWcid,
                Script = Script,
                MarkScript = MarkScript,
                PlayerRadius = PlayerRadius,
                EdgeInset = EdgeInset,
            };
        }

        private static WispFire ParseFire(string text)
            => text == "random" ? WispFire.Random : text == "sequence" ? WispFire.Sequence : WispFire.Unison;

        /// <summary>The centre of ring and diagonal: cx/cy, each defaulting to the region's own centre.</summary>
        public Vector2 CentreFor(HazardRegion region)
            => new Vector2(CentreX ?? region.CenterX, CentreY ?? region.CenterY);

        /// <summary>The ring radius: ring_radius, else half the region's inscribed radius.</summary>
        public float RingRadiusFor(HazardRegion region) => RingRadius ?? region.InscribedRadius * 0.5f;
    }

    /// <summary>One step of a run's timeline, relative to the moment the step's glide starts.</summary>
    public readonly struct WispStepTimeline
    {
        public WispStepTimeline(double arrive, IReadOnlyList<double> burstAt, double end)
        {
            Arrive = arrive;
            BurstAt = burstAt;
            End = end;
        }

        /// <summary>The wisps are at their spots (the glide is over); the telegraph starts.</summary>
        public double Arrive { get; }

        /// <summary>When each wisp's pillar starts, in formation order. Never earlier than Arrive + hold.</summary>
        public IReadOnlyList<double> BurstAt { get; }

        /// <summary>When the last pillar stops burning and the next step (or the rest) begins.</summary>
        public double End { get; }
    }

    /// <summary>
    /// Every decision of the wisps mechanic that is not geometry (<see cref="WispFormations"/>): the settings'
    /// refusals (Validate), the fire order, the timeline, which wisp goes to which spot, and PlaceWisp over a whole
    /// formation. PURE (shared invariant 6): a passed-in Random, no Player, no PropertyManager.
    /// </summary>
    public static class WispRules
    {
        public const string Scatter = "scatter";
        public const string Row = "row";
        public const string Column = "column";
        public const string Diagonal = "diagonal";
        public const string Ring = "ring";
        public const string Sweep = "sweep";
        public const string Mixed = "mixed";

        /// <summary>The registry's pattern list: the FIRST is the default.</summary>
        public static readonly IReadOnlyList<string> Patterns = new[] { Scatter, Row, Column, Diagonal, Ring, Sweep, Mixed };

        // ---- fire order and timeline ----------------------------------------------------------------------

        /// <summary>
        /// Each wisp's extra delay AFTER the hold, in formation order. The hold is a floor, never a ceiling: whatever the
        /// fire mode, no pillar starts earlier than <c>hold</c> seconds after the wisps arrive, so the telegraph is
        /// always the full reaction time. unison: all 0. random: uniform in [0, hold). sequence: i x seq_gap.
        /// </summary>
        public static double[] FireOffsets(WispFire fire, int count, double hold, double seqGap, Random rng)
        {
            var offsets = new double[Math.Max(0, count)];

            for (var i = 0; i < offsets.Length; i++)
            {
                switch (fire)
                {
                    case WispFire.Random:
                        offsets[i] = rng == null ? 0.0 : rng.NextDouble() * Math.Max(0.0, hold);
                        break;

                    case WispFire.Sequence:
                        offsets[i] = i * Math.Max(0.0, seqGap);
                        break;

                    default:
                        offsets[i] = 0.0;
                        break;
                }
            }

            return offsets;
        }

        /// <summary>
        /// One step: glide <paramref name="move"/> s, hold <paramref name="hold"/> s (the telegraph), then wisp i
        /// bursts at move + hold + offsets[i] and burns <paramref name="burst"/> s. Times are from the glide start.
        /// </summary>
        public static WispStepTimeline Timeline(double move, double hold, IReadOnlyList<double> offsets, double burst)
        {
            var at = new double[offsets?.Count ?? 0];
            var last = move + hold;

            for (var i = 0; i < at.Length; i++)
            {
                at[i] = move + hold + Math.Max(0.0, offsets[i]);
                last = Math.Max(last, at[i]);
            }

            return new WispStepTimeline(move, at, at.Length == 0 ? move + hold : last + burst);
        }

        // ---- who goes where -------------------------------------------------------------------------------

        /// <summary>
        /// For each target (in formation order) the index of the nearest wisp not yet taken, or -1 when there are
        /// more targets than wisps. Ties go to the lower index. Deterministic.
        /// </summary>
        public static int[] Assign(IReadOnlyList<Vector2> wisps, IReadOnlyList<Vector2> targets)
        {
            var result = new int[targets.Count];
            var used = new bool[wisps.Count];

            for (var j = 0; j < targets.Count; j++)
            {
                var best = -1;
                var bestDistance = float.MaxValue;

                for (var w = 0; w < wisps.Count; w++)
                {
                    if (used[w])
                        continue;

                    var d = Vector2.DistanceSquared(wisps[w], targets[j]);

                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = w;
                    }
                }

                result[j] = best;

                if (best >= 0)
                    used[best] = true;
            }

            return result;
        }

        /// <summary>
        /// THE WISP RULE over a whole formation (shared invariant 10): every wisp target goes through
        /// <see cref="HazardGround.PlaceWisp"/>, so a wisp stands at the last open point before a solid and its pillar
        /// fires at the true target (raised onto the solid's top). <paramref name="hasCell"/> is HazardContext.HasCell
        /// without the raise.
        /// </summary>
        public static HazardGround.WispPlacement[] Place(IReadOnlyList<Vector3> currents, IReadOnlyList<Vector3> desired, Func<Vector3, bool> hasCell, float clearance)
        {
            var placed = new HazardGround.WispPlacement[desired.Count];

            for (var i = 0; i < placed.Length; i++)
                placed[i] = HazardGround.PlaceWisp(currents[i], desired[i], hasCell, clearance);

            return placed;
        }

        // ---- which patterns a mixed run may pick ----------------------------------------------------------

        /// <summary>A row runs west-east, so it is measured from the north or south side: dir n, s or random.</summary>
        public static bool RowAllows(string dir) => dir == "n" || dir == "s" || dir == "random";

        /// <summary>A column runs north-south, so it is measured from the east or west side: dir e, w or random.</summary>
        public static bool ColumnAllows(string dir) => dir == "e" || dir == "w" || dir == "random";

        /// <summary>The patterns <c>mixed</c> picks from for this region and settings: scatter and diagonal always; row and column when dir allows their axis; ring and sweep when they fit.</summary>
        public static IReadOnlyList<string> Pool(HazardRegion region, WispSettings s)
        {
            var pool = new List<string> { Scatter };

            if (RowAllows(s.Dir))
                pool.Add(Row);

            if (ColumnAllows(s.Dir))
                pool.Add(Column);

            pool.Add(Diagonal);

            if (RingProblem(region, s) == null)
                pool.Add(Ring);

            if (SweepProblem(region, s) == null)
                pool.Add(Sweep);

            return pool;
        }

        // ---- Validate -------------------------------------------------------------------------------------

        private static readonly string[] NumberKeys =
        {
            "scale", "move", "hold", "burst", "rest", "pillar_radius", "seq_gap", "sweep_move", "ring_radius", "cx", "cy",
            "scatter_cover", "wisp_clear", HazardDefaults.EscapeSpeedKey, HazardDefaults.PlayerRadiusKey, HazardDefaults.EdgeInsetKey, HazardDefaults.PctKey,
            "move_end", "hold_end", "rest_end", "follow_lag", "ceiling_beam", "strike_delay",
        };

        private static readonly string[] WholeNumberKeys = { "n", "cycles", "steps", "wisp_wcid" };

        /// <summary>
        /// The largest wisp scale whose body (origin at its feet, <see cref="HazardDefaults.WispHeightAtScale1"/> x scale
        /// tall) stays <see cref="HazardDefaults.WispCeilingMargin"/> under the region's ceiling at its hazard height, or
        /// null when the region has no ceiling. Plane regions only measure the hazard z at the centre (a plane hazard z
        /// is the same everywhere; a terrain region's ceiling is checked against its ground plane). Pinnacle: hazard z
        /// -47.25, ceiling -42.05: (5.20 - 0.10) / 1.83 = 2.79.
        /// </summary>
        public static float? MaxScaleUnderCeiling(HazardRegion region)
        {
            if (region?.CeilingZ == null)
                return null;

            var room = region.CeilingZ.Value - HazardDefaults.WispCeilingMargin - region.HazardZ(region.CenterX, region.CenterY);
            return room / HazardDefaults.WispHeightAtScale1;
        }

        /// <summary>One-line refusal when a wisp of <paramref name="scale"/> would reach the region's ceiling, else null.</summary>
        public static string CeilingProblem(HazardRegion region, float scale)
        {
            var max = MaxScaleUnderCeiling(region);

            if (max == null || scale <= max.Value + 1e-4f)
                return null;

            var inv = CultureInfo.InvariantCulture;
            var top = region.HazardZ(region.CenterX, region.CenterY) + HazardDefaults.WispHeightAtScale1 * scale;

            return max.Value <= 0f
                ? string.Format(inv, "the region's hazard height leaves no room for a wisp under its ceiling {0:0.##}; lower the hazard height", region.CeilingZ.Value)
                : string.Format(inv, "scale={0:0.##} wisps are {1:0.##} m tall and would reach z {2:0.##}, within {3:0.##} m of the ceiling {4:0.##}; the largest scale that fits is {5:0.##}",
                    scale, HazardDefaults.WispHeightAtScale1 * scale, top, HazardDefaults.WispCeilingMargin, region.CeilingZ.Value, Math.Floor(max.Value * 100f) / 100f);
        }

        /// <summary>
        /// The mechanic's pre-flight (shared invariant 8): null when the run can start, else ONE line. The bounds
        /// (g = 2 x player_radius + 0.5, pr = pillar_radius):
        ///   numbers     every numeric arg that is given must parse (n, cycles, steps, wisp_wcid as whole numbers); n >= 1
        ///   fire, dir   one of the listed words; row takes dir n|s|random, column e|w|random
        ///   region      usable radius (InscribedRadius - edge_inset) >= pr + player_radius + g / 2
        ///   exclusions  every Exclusion's inscribed radius <= pr + player_radius, so a pillar next to it reaches everywhere inside it
        ///               (no permanent safe spot); and the usable area must have a placeable spot
        ///   hold        >= 0.5 + (pr + player_radius) / escape_speed: time to react and run out of a pillar. The floor applies to the
        ///               lesser of hold and hold_end; hold_floor=0 lifts it for both (the 0.2 s clamp still holds)
        ///   centre      (cx, cy) inside the region
        ///   run time    cycles x steps x (move + hold + burst + rest) &lt;= <see cref="WispSettings.MaxRunSeconds"/>, with the START
        ///               timings (the "_end" keys are not counted: a ramp towards slower timings can run past it)
        ///   scatter     floor(scatter_cover x usable area / (pi x (pr + player_radius)^2)) >= 1
        ///   ring        ring_radius &lt;= distance from (cx, cy) to the nearest edge - edge_inset, and it holds >= min(n, 3) wisps
        ///               with g between neighbouring discs (spacing 2 x ring_radius x sin(pi / k) >= 2 x pr + g)
        ///   sweep       a line along the dir axis at least 2 x (2 x pr + g) long: one wisp plus the gap lane
        /// Rows, columns and diagonals cannot trap anyone by construction (the count is cut until every neighbouring pair of
        /// discs is g apart), so they have no bound of their own.
        /// </summary>
        public static string Validate(HazardRegion region, HazardArgs args, string pattern)
        {
            if (region == null)
                return "no region";

            args ??= HazardArgs.Empty;
            var inv = CultureInfo.InvariantCulture;

            var notNumeric = args.FirstNonNumeric(NumberKeys, WholeNumberKeys);

            if (notNumeric != null)
                return notNumeric;

            if (args.Has("n") && int.Parse(args.GetString("n"), NumberStyles.Integer, inv) < 1)
                return $"n={args.GetString("n")}: a run needs at least 1 wisp";

            var s = WispSettings.From(args);
            var asked = pattern ?? Scatter;

            var bad = BadChoice(args, "fire", "unison", "random", "sequence") ?? BadChoice(args, "dir", "n", "s", "e", "w", "random");

            if (bad != null)
                return bad;

            // yes/no choices: a typo is refused, not silently read as the default
            var badFlag = BadFlag(args, "hold_floor") ?? BadFlag(args, "follow") ?? BadFlag(args, "mark_dest");

            if (badFlag != null)
                return badFlag;

            var script = args.GetString("script");

            if (script != null && !HazardToolRoutes.TryParseScript(script, out _))
                return $"script='{script}' is not a script id (decimal or 0x hex)";

            var markScript = args.GetString("mark_script");

            if (markScript != null && !HazardToolRoutes.TryParseScript(markScript, out _))
                return $"mark_script='{markScript}' is not a script id (decimal or 0x hex)";

            if (args.Has("ceiling_beam"))
            {
                var beamSeconds = double.Parse(args.GetString("ceiling_beam"), NumberStyles.Float, inv);

                if (beamSeconds < 0.0 || beamSeconds > 5.0)
                    return $"ceiling_beam={args.GetString("ceiling_beam")}: must be 0..5 seconds";
            }

            var beamScript = args.GetString("beam_script");

            if (beamScript != null && !HazardToolRoutes.TryParseScript(beamScript, out _))
                return $"beam_script='{beamScript}' is not a script id (decimal or 0x hex)";

            var ceiling = CeilingProblem(region, s.Scale);

            if (ceiling != null)
                return ceiling;

            if (asked == Row && !RowAllows(s.Dir))
                return $"row runs west-east, so it is measured from the north or south side: dir must be n, s or random (got {s.Dir})";

            if (asked == Column && !ColumnAllows(s.Dir))
                return $"column runs north-south, so it is measured from the east or west side: dir must be e, w or random (got {s.Dir})";

            var usable = region.InscribedRadius - s.EdgeInset;
            var need = s.PillarRadius + s.PlayerRadius + s.MinGap * 0.5f;

            if (usable < need)
                return string.Format(inv,
                    "region too small: its usable radius is {0:0.##} m (inscribed {1:0.##} - edge_inset {2:0.##}) but one pillar needs {3:0.##} m (pillar_radius {4:0.##} + player_radius {5:0.##} + half the {6:0.##} m corridor); lower pillar_radius or edge_inset, or use a bigger region",
                    usable, region.InscribedRadius, s.EdgeInset, need, s.PillarRadius, s.PlayerRadius, s.MinGap);

            var reach = s.PillarRadius + s.PlayerRadius;

            foreach (var e in region.Exclusions)
            {
                var inscribed = ExclusionInscribedRadius(e);

                if (inscribed > reach + 1e-4f)
                    return string.Format(inv,
                        "an exclusion of inscribed radius {0:0.##} m is bigger than a pillar's reach {1:0.##} m (pillar_radius {2:0.##} + player_radius {3:0.##}): players could stand in it forever; shrink it or raise pillar_radius",
                        inscribed, reach, s.PillarRadius, s.PlayerRadius);
            }

            if (!HasPlaceableSpot(region, s.EdgeInset))
                return "no spot of this region is both inside edge_inset and outside every exclusion: the wisps would have nowhere to fire";

            // The floor applies to the HARDEST hold of the run: the lesser of hold and hold_end. hold_floor=0 lifts it for both.
            var floor = s.EscapeHoldFloor;

            if (s.HoldFloor)
            {
                var hardKey = s.HoldEnd.HasValue && s.HoldEnd.Value < s.Hold ? "hold_end" : "hold";
                var hard = hardKey == "hold_end" ? s.HoldEnd.Value : s.Hold;

                if (hard < floor - 1e-9)
                    return string.Format(inv,
                        "{5}={0:0.###} s is too short a telegraph: it must be at least {1:0.###} s (0.5 + (pillar_radius {2:0.##} + player_radius {3:0.##}) / escape_speed {4:0.##}) for a player to react and run out of a pillar",
                        hard, floor, s.PillarRadius, s.PlayerRadius, s.EscapeSpeed, hardKey);
            }

            // the follower trails by follow_lag: under the escape floor it would stand on a player before they could react
            if (s.Follow && s.HoldFloor && s.FollowLag < floor - 1e-9)
                return string.Format(inv,
                    "follow_lag={0:0.###} s is too short: it must be at least {1:0.###} s (0.5 + (pillar_radius {2:0.##} + player_radius {3:0.##}) / escape_speed {4:0.##}) for a player to react and run out of the follower's pillar; set hold_floor=0 to allow it",
                    s.FollowLag, floor, s.PillarRadius, s.PlayerRadius, s.EscapeSpeed);

            var centre = s.CentreFor(region);

            if (!region.Contains(centre.X, centre.Y))
                return string.Format(inv, "centre cx/cy=({0:0.##}, {1:0.##}) is outside the region; it must be inside ({2})", centre.X, centre.Y, region.Describe());

            var steps = StepsFor(region, s, asked);
            var total = s.Cycles * (double)steps * (s.Move + s.Hold + s.Burst + s.Rest);

            if (total > WispSettings.MaxRunSeconds)
                return string.Format(inv,
                    "this run would last {0:0} s ({1} cycles x {2} step(s) x {3:0.##} s of move + hold + burst + rest); the limit is {4:0} s: lower cycles or the timings",
                    total, s.Cycles, steps, s.Move + s.Hold + s.Burst + s.Rest, WispSettings.MaxRunSeconds);

            if (asked == Scatter || asked == Mixed)
            {
                var problem = ScatterProblem(region, s);

                if (problem != null)
                    return problem;
            }

            if (asked == Ring || (asked == Mixed && s.RingRadius.HasValue))
            {
                var problem = RingProblem(region, s);

                if (problem != null)
                    return problem;
            }

            if (asked == Sweep)
            {
                var problem = SweepProblem(region, s);

                if (problem != null)
                    return problem;
            }

            return null;
        }

        /// <summary>
        /// The most cycles that fit in <paramref name="seconds"/> (default the wisp run cap, <see cref="WispSettings.MaxRunSeconds"/>):
        /// floor(seconds / (steps x (move + hold + burst + rest))) with the START timings, the same sum <see cref="Validate"/>
        /// bounds. Not clamped to the cycles arg's own 1..500 range; 0 when not even one cycle fits. A null pattern is scatter.
        /// </summary>
        public static int MaxCyclesWithin(HazardRegion region, HazardArgs args, string pattern, double seconds = WispSettings.MaxRunSeconds)
        {
            if (region == null || !(seconds > 0.0) || !double.IsFinite(seconds))
                return 0;

            var s = WispSettings.From(args ?? HazardArgs.Empty);
            var steps = StepsFor(region, s, pattern ?? Scatter);
            var perCycle = steps * (s.Move + s.Hold + s.Burst + s.Rest);

            if (!(perCycle > 0.0))
                return 0;

            return (int)Math.Min(int.MaxValue, Math.Floor(seconds / perCycle + 1e-9));
        }


        /// <summary>A yes/no arg that is given but is not one of HazardArgs.GetBool's words; else null.</summary>
        private static string BadFlag(HazardArgs args, string key)
        {
            var raw = args.GetString(key);

            if (raw == null)
                return null;

            return Array.IndexOf(new[] { "1", "true", "on", "yes", "0", "false", "off", "no" }, raw.Trim().ToLowerInvariant()) >= 0
                ? null
                : $"{key}='{raw}' is not one of true|false";
        }

        private static string BadChoice(HazardArgs args, string key, params string[] allowed)
        {
            var raw = args.GetString(key);

            if (raw == null)
                return null;

            foreach (var a in allowed)
                if (string.Equals(a, raw, StringComparison.OrdinalIgnoreCase))
                    return null;

            return $"{key}='{raw}' is not one of {string.Join("|", allowed)}";
        }

        /// <summary>The radius of the biggest circle that fits inside the exclusion.</summary>
        public static float ExclusionInscribedRadius(HazardExclusion e)
            => e.Shape == HazardShape.Circle ? e.Radius : Math.Min(e.MaxX - e.MinX, e.MaxY - e.MinY) * 0.5f;

        /// <summary>Is any sampled spot of the usable area inside the shape and outside every exclusion? (A 40 x 40 grid over the usable area.)</summary>
        public static bool HasPlaceableSpot(HazardRegion region, float inset)
        {
            var arena = WispArena.Of(region, inset);

            if (!(arena.Area > 0f))
                return false;

            const int n = 40;

            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                {
                    var x = arena.MinX + (arena.MaxX - arena.MinX) * (i + 0.5f) / n;
                    var y = arena.MinY + (arena.MaxY - arena.MinY) * (j + 0.5f) / n;

                    if (arena.Contains(x, y) && region.IsPlaceable(x, y))
                        return true;
                }
            }

            return false;
        }

        /// <summary>How many steps one cycle of <paramref name="pattern"/> has at most (1, or a sweep's rows; mixed counts a sweep when it can pick one).</summary>
        public static int StepsFor(HazardRegion region, WispSettings s, string pattern)
        {
            var sweeps = pattern == Sweep || (pattern == Mixed && SweepProblem(region, s) == null);

            if (!sweeps)
                return 1;

            var arena = WispArena.Of(region, s.EdgeInset);
            var sides = s.Dir == "random"
                ? new[] { WispCompass.N, WispCompass.E, WispCompass.S, WispCompass.W }
                : new[] { WispFormations.ParseSide(s.Dir) };

            var most = 1;

            foreach (var side in sides)
                most = Math.Max(most, WispFormations.SweepStepCount(arena, s, side));

            return most;
        }

        /// <summary>How many wisps scatter may place: floor(scatter_cover x usable area / (pi x (pillar_radius + player_radius)^2)).</summary>
        public static int ScatterLimit(HazardRegion region, WispSettings s)
        {
            var arena = WispArena.Of(region, s.EdgeInset);
            var disc = Math.PI * Math.Pow(s.PillarRadius + s.PlayerRadius, 2.0);

            return (int)Math.Floor(s.ScatterCover * arena.Area / disc + 1e-9);
        }

        public static string ScatterProblem(HazardRegion region, WispSettings s)
        {
            if (ScatterLimit(region, s) >= 1)
                return null;

            return string.Format(CultureInfo.InvariantCulture,
                "pillar_radius={0:0.##} is too big for scatter here: one pillar (radius {0:0.##} + player {1:0.##}) would cover more than {2:0.#}% of the {3:0.#} m2 usable area; lower pillar_radius or raise scatter_cover",
                s.PillarRadius, s.PlayerRadius, s.ScatterCover * 100.0, WispArena.Of(region, s.EdgeInset).Area);
        }

        /// <summary>The most wisps a ring of <paramref name="ringRadius"/> holds with <paramref name="minSpacing"/> between neighbouring centres: floor(pi / asin(minSpacing / 2r)).</summary>
        public static int RingCapacity(float ringRadius, float minSpacing)
        {
            if (!(ringRadius > 0f) || !(minSpacing > 0f))
                return 0;

            var x = minSpacing / (2.0 * ringRadius);

            if (x > 1.0)
                return 1;

            return (int)Math.Floor(Math.PI / Math.Asin(x) + 1e-9);
        }

        public static string RingProblem(HazardRegion region, WispSettings s)
        {
            var inv = CultureInfo.InvariantCulture;
            var centre = s.CentreFor(region);
            var rr = s.RingRadiusFor(region);
            var room = region.DistanceToEdge(centre.X, centre.Y) - s.EdgeInset;

            if (rr > room + 1e-4f)
                return string.Format(inv, "ring_radius={0:0.##} does not fit round ({1:0.##}, {2:0.##}): the nearest edge leaves only {3:0.##} m after edge_inset {4:0.##}; lower ring_radius or move cx/cy",
                    rr, centre.X, centre.Y, room, s.EdgeInset);

            var capacity = RingCapacity(rr, s.MinSpacing);
            var want = Math.Min(s.Count, 3);

            if (capacity < want)
                return string.Format(inv, "ring_radius={0:0.##} holds only {1} wisp(s) with a {2:0.##} m corridor between {3:0.##}-radius pillars; {4} need ring_radius of at least {5:0.##}",
                    rr, capacity, s.MinGap, s.PillarRadius, want, s.MinSpacing / (2.0 * Math.Sin(Math.PI / want)));

            return null;
        }

        /// <summary>A sweep line leaves a gap lane, so it needs room for at least 2 slots of MinSpacing along the axis dir uses.</summary>
        public static string SweepProblem(HazardRegion region, WispSettings s)
        {
            var arena = WispArena.Of(region, s.EdgeInset);
            var longest = s.Dir == "random"
                ? Math.Min(arena.LineLength(WispCompass.N), arena.LineLength(WispCompass.E))
                : arena.LineLength(WispFormations.ParseSide(s.Dir));

            if (longest >= 2f * s.MinSpacing - 1e-4f)
                return null;

            return string.Format(CultureInfo.InvariantCulture,
                "sweep needs a line at least {0:0.##} m long (a wisp plus its gap lane, 2 x {1:0.##} m); the longest line along dir={2} here is {3:0.##} m",
                2f * s.MinSpacing, s.MinSpacing, s.Dir, longest);
        }

        // ---- the pillar host ----------------------------------------------------------------------------------

        /// <summary>
        /// Spawns a pillar: first at <paramref name="pillarPoint"/>, and if that host would not spawn, ONCE MORE at the wisp's
        /// <paramref name="standPoint"/>. Returns null when both fail: the caller logs the coordinates and deals NO damage from
        /// that slot (damage nobody can see is unfair). <paramref name="usedAt"/> is the point that worked.
        /// </summary>
        public static T SpawnPillar<T>(Func<Vector3, T> spawn, Vector3 pillarPoint, Vector3 standPoint, out Vector3 usedAt) where T : class
        {
            usedAt = pillarPoint;
            var host = spawn(pillarPoint);

            if (host != null)
                return host;

            usedAt = standPoint;
            return spawn(standPoint);
        }
    }
}
