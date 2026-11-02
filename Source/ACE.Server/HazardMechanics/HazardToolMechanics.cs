using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// /hazard move - THE LIVE MOVEMENT GATE, v2: self-explaining and self-measuring. Runs up to five ISOLATED
    /// cases one after another (one hazard on screen at a time), <c>hold</c> seconds apart. Before each case it says
    /// in chat exactly what should happen (body, direction, from/to, height, speed, duration, what a pass looks
    /// like), spawns the hazard at the start, and puts a pillar at the start and the end of the path; the hazard
    /// starts moving <c>lead</c> seconds later. After the case it is destroyed.
    ///
    /// THE CASES (picked from the region by <see cref="HazardMoveGate.Plan"/>, from what actually has a cell at the
    /// hazard height - nothing is hard-coded to a landblock):
    ///   1. wisp, straight leg through OPEN air only (no solid on or within 2.5 m of the line);
    ///   2. ball, the same open line;
    ///   3. ball, a line that crosses a solid (a block or bridge top);
    ///   4. wisp, towards the MIDDLE of that solid - it must STOP at the solid's edge (wisps never enter solids,
    ///      HazardGround.PlaceWisp) while a third pillar marks the true target on the solid's top;
    ///   5. ball, the open line out and straight back with NO pause (the junction is the framework's leg-end snap
    ///      followed by the next leg in the same driver tick).
    /// A case whose line the region does not have (e.g. no solid anywhere) is announced as skipped.
    ///
    /// MEASUREMENT, in the server log, every line carrying "move-case=N leg=M" after the run prefix:
    ///   phase=sent  every message actually sent for the leg (CreateObject at spawn, each VectorUpdate, the
    ///               leg-end UpdatePosition(teleport) + zero VectorUpdate) - from HazardContext.TraceTag;
    ///   phase=mid   half way through the leg; phase=end  on the tick the leg is found over, BEFORE the snap;
    ///   phase=rest  1 s after the last leg's snap.
    /// mid/end/rest print computed vs predicted-client vs server position, drift, cell, landblock, sent and
    /// physics velocity. The server Location is EXPECTED to stay at the spawn/snap point during a leg (the server
    /// never simulates a hazard), so mid-leg drift = distance flown; a server position that moves DURING a leg, or a
    /// landblock change, is the failure.
    ///
    /// Args (all optional): case=0 (1..5 runs only that case) speed=4 m/s (0.5..20) hold=2 s between cases
    /// (0.5..10) lead=1 s markers-before-motion (0.5..5) max_len=20 m (6..60) ball_z=2.0 (the ball's hover above GroundZ, 2..10; HazardDefaults.BallHover) wisp_clear=0.5 m (0..3, how far a blocked wisp stands back from the solid)
    /// ball_radius (default 0.80 x the ball's scale) wisp_radius=1.0 (0.1..5) hit=0 script (marker pillar)
    /// wisp_wcid / ball_wcid (default the library's content wcids) edge_inset (framework, default 2 m).
    /// </summary>
    public sealed class HazardMoveTestMechanic : IHazardMechanic
    {
        private IReadOnlyList<MoveGateCase> cases;
        private readonly List<int> order = new List<int>();
        private int next;

        private HazardHandle body;
        private int caseSerial;

        private float speed;
        private double hold;
        private double lead;
        private float ballZ;
        private float wispClear;
        private float ballRadius;
        private float wispRadius;
        private bool hit;
        private uint markerScript;
        private string phase = "planning";

        public string Kind => HazardMechanicRegistry.Move;

        public IReadOnlyList<string> Patterns => Array.Empty<string>();

        public IEnumerable<uint> RequiredWcids(HazardArgs args) => new[] { WispWcid(args), BallWcid(args), HazardDefaults.EffectHostWcid };

        private static uint WispWcid(HazardArgs args) => (uint)args.GetInt("wisp_wcid", (int)HazardDefaults.EmberWispWcid, 1, int.MaxValue);

        private static uint BallWcid(HazardArgs args) => (uint)args.GetInt("ball_wcid", (int)HazardDefaults.RollingDeathRedWcid, 1, int.MaxValue);

        public string Validate(HazardRegion region, HazardArgs args, string pattern)
        {
            var inset = args.GetDouble(HazardDefaults.EdgeInsetKey, HazardDefaults.EdgeInset, HazardDefaults.MinEdgeInset, HazardDefaults.MaxEdgeInset);

            if (region.InscribedRadius - inset < 3.0)
                return $"region is too small for the movement test once edge_inset={inset:0.#} m is kept clear of its walls";

            return null;
        }

        public void Start(HazardContext ctx)
        {
            var a = ctx.Args;

            speed = (float)a.GetDouble("speed", 4.0, 0.5, 20.0);
            hold = a.GetDouble("hold", 2.0, 0.5, 10.0);
            // Floor 0.5 s: the first leg starts several driver ticks after the spawn (the framework also defers a
            // same-tick MoveLeg; this keeps the test from depending on that).
            lead = a.GetDouble("lead", 1.0, 0.5, 5.0);
            ballZ = (float)a.GetDouble(HazardDefaults.BallHoverKey, HazardDefaults.BallHover, HazardDefaults.MinBallHover, HazardDefaults.MaxBallHover);
            wispClear = (float)a.GetDouble("wisp_clear", 0.5, 0.0, 3.0);
            ballRadius = (float)a.GetDouble("ball_radius", HazardDefaults.RollingDeathRadiusAtScale1, 0.1, 5.0);
            wispRadius = (float)a.GetDouble("wisp_radius", 1.0, 0.1, 5.0);
            hit = a.GetBool("hit", false);
            markerScript = HazardToolRoutes.ParseScript(a.GetString("script"), HazardDefaults.PillarScript);

            var maxLength = (float)a.GetDouble("max_len", 20.0, 6.0, 60.0);
            var only = a.GetInt("case", 0, 0, HazardMoveGate.CaseCount);

            // Solid = no cell at the hazard height (moving bodies are never raised). Memoised: the planner probes a
            // half-metre grid, and the margin probes land on the same grid.
            var memo = new Dictionary<(int, int), bool>();

            bool Solid(Vector2 p)
            {
                var key = ((int)MathF.Round(p.X * 4f), (int)MathF.Round(p.Y * 4f));

                if (!memo.TryGetValue(key, out var s))
                {
                    s = !ctx.HasCell(new Vector3(p, ctx.HazardZ(p.X, p.Y)));
                    memo[key] = s;
                }

                return s;
            }

            cases = HazardMoveGate.Plan(ctx.Region, ctx.EdgeInset, Solid, maxLength);

            for (var n = 1; n <= HazardMoveGate.CaseCount; n++)
                if (only == 0 || only == n)
                    order.Add(n);

            ctx.Log(FormattableString.Invariant($"move gate v2 plan: inset={ctx.EdgeInset:0.0} speed={speed:0.0} probes={memo.Count} cases={string.Join(" | ", Summaries())}"));

            ctx.Say(FormattableString.Invariant(
                $"[hazard #{ctx.Run.Id}] MOVE GATE v2: {order.Count} case(s), one hazard at a time, {hold:0.#} s apart. Each case is announced first; pillars mark its start and end {lead:0.#} s before it moves. Server log tag: move-case=N."));

            phase = "starting";
            ctx.Schedule(0.5, () => RunNext(ctx));
        }

        private IEnumerable<string> Summaries()
        {
            foreach (var c in cases)
                yield return c.Describe();
        }

        private void RunNext(HazardContext ctx)
        {
            if (next >= order.Count)
            {
                phase = "done";
                ctx.Say($"[hazard #{ctx.Run.Id}] MOVE GATE v2 complete. Grep the server log for \"run=#{ctx.Run.Id} kind=move move-case=\".");
                ctx.End(HazardOutcome.Completed, "all cases run");
                return;
            }

            var c = cases[order[next++] - 1];
            var serial = ++caseSerial;

            if (c.Skip != null)
            {
                phase = $"case {c.Number} skipped";
                ctx.Say($"[hazard #{ctx.Run.Id}] Case {c.Number}/{HazardMoveGate.CaseCount} SKIPPED: {c.Skip}.");
                ctx.Log($"move-case={c.Number} skipped: {c.Skip}");
                ctx.Schedule(0.5, () => RunNext(ctx));
                return;
            }

            var points = new List<Vector3>(c.Points.Count);

            foreach (var p in c.Points)
                points.Add(BodyPoint(ctx, c.Body, p));

            // WISPS NEVER ENTER SOLIDS: a wisp whose target is inside a solid stands at the solid's edge and its
            // pillar fires at the true target, raised onto the solid's top (HazardGround.PlaceWisp).
            Vector3? pillar = null;

            if (c.Body == HazardMoveGate.Wisp && c.LegCount == 1)
            {
                var placed = HazardGround.PlaceWisp(points[0], points[1], p => ctx.HasCell(p), wispClear);

                ctx.Log(FormattableString.Invariant(
                    $"move-case={c.Number} leg=1 phase=plan wisp target=({points[1].X:0.00},{points[1].Y:0.00},{points[1].Z:0.00}) stand=({placed.Stand.X:0.00},{placed.Stand.Y:0.00},{placed.Stand.Z:0.00}) pillar=({placed.Pillar.X:0.00},{placed.Pillar.Y:0.00},{placed.Pillar.Z:0.00}) blocked={placed.Blocked} pillar_found={placed.PillarFound}"));

                if (placed.Blocked || !placed.Pillar.Equals(points[1]))
                    pillar = placed.Pillar;

                points[1] = placed.Stand;
            }

            var durations = new double[c.LegCount];
            var total = 0.0;

            for (var m = 1; m <= c.LegCount; m++)
            {
                durations[m - 1] = LegSeconds(new Vector2(points[m - 1].X, points[m - 1].Y), new Vector2(points[m].X, points[m].Y), speed);
                total += durations[m - 1];
            }

            ctx.Say($"[hazard #{ctx.Run.Id}] {HazardMoveGate.Announce(c, points, speed, durations, pillar)}");

            ctx.TraceTag = $"move-case={c.Number} leg=0";

            var wcid = c.Body == HazardMoveGate.Wisp ? WispWcid(ctx.Args) : BallWcid(ctx.Args);

            body = ctx.Spawn(new HazardSpawnSpec
            {
                Wcid = wcid,
                Position = points[0],
                Moving = true,
                Tag = c.Body,
                HitRadius = c.Body == HazardMoveGate.Wisp ? wispRadius : ballRadius,
            });

            if (body == null)
            {
                phase = $"case {c.Number} spawn refused";
                ctx.Say($"[hazard #{ctx.Run.Id}] Case {c.Number}: the {c.Body} could not be spawned at its start point (see the server log). Moving on.");
                ctx.TraceTag = null;
                ctx.Schedule(hold, () => RunNext(ctx));
                return;
            }

            // Roll radius: the measured 0.80 m at scale 1 times the ball's actual scale (2.25 -> 1.8 m), unless given.
            if (c.Body == HazardMoveGate.Ball && !ctx.Args.Has("ball_radius") && body.Object != null)
                ballRadius = HazardDefaults.RollingDeathRadiusAtScale1 * (body.Object.ObjScale ?? 1f);

            LogSent(ctx, c.Number, 0, "CreateObject", body);

            // Markers at the exact start and end points, at the body's own height (an effect origin may be raised up
            // to 1.5 m out of a solid; the start/end points are open by construction). They outlive the case.
            var markerSeconds = lead + total + 2.0;
            ctx.SpawnTimedEffect(points[0], markerScript, Quaternion.Identity, markerSeconds, 1f, "marker-start");
            ctx.SpawnTimedEffect(points[1], markerScript, Quaternion.Identity, markerSeconds, 1f, "marker-end");

            if (pillar.HasValue)
                ctx.SpawnTimedEffect(pillar.Value, markerScript, Quaternion.Identity, markerSeconds, 1f, "marker-target");

            phase = $"case {c.Number} lead-in";
            ctx.Schedule(lead, () => StartLeg(ctx, c, serial, points, durations, 1));

            // Watchdog: a body lost mid-case (a /reload-landblock) never reaches its leg end.
            ctx.Schedule(lead + total + 5.0, () => FinishCase(ctx, c, serial, "watchdog"));
        }

        private void StartLeg(HazardContext ctx, MoveGateCase c, int serial, IReadOnlyList<Vector3> points, double[] durations, int m)
        {
            if (serial != caseSerial)
                return;

            if (body == null || !body.Alive)
            {
                ctx.Log($"move-case={c.Number} leg={m} phase=lost the {c.Body} died before the leg started");
                FinishCase(ctx, c, serial, "lost");
                return;
            }

            ctx.TraceTag = $"move-case={c.Number} leg={m}";
            phase = $"case {c.Number} leg {m}/{c.LegCount}";

            var duration = durations[m - 1];
            var isWisp = c.Body == HazardMoveGate.Wisp;

            var options = new HazardMoveOptions
            {
                Follow = isWisp ? HazardFollow.HazardHeight : HazardFollow.Ground,
                GroundOffset = isWisp ? 0f : ballZ,
                RollRadius = isWisp ? 0f : ballRadius,
                OnLegEnd = (h, path) =>
                {
                    LogPhase(ctx, c.Number, m, "end", h, path);

                    if (m < c.LegCount)
                        ctx.Schedule(0.0, () => StartLeg(ctx, c, serial, points, durations, m + 1)); // same tick, after the snap
                    else
                        ctx.Schedule(1.0, () => FinishCase(ctx, c, serial, "done"));
                },
            };

            if (!ctx.MoveLeg(body, points[m], duration, options))
            {
                FinishCase(ctx, c, serial, "MoveLeg refused");
                return;
            }

            var handle = body;
            ctx.Schedule(duration * 0.5, () =>
            {
                if (serial == caseSerial)
                    LogPhase(ctx, c.Number, m, "mid", handle, handle.Path);
            });
        }

        private void FinishCase(HazardContext ctx, MoveGateCase c, int serial, string why)
        {
            if (serial != caseSerial)
                return;

            caseSerial++; // anything still scheduled for this case is now stale

            if (body != null)
            {
                LogPhase(ctx, c.Number, c.LegCount, "rest", body, body.Path);
                ctx.Destroy(body);
                body = null;
            }

            ctx.TraceTag = null;
            phase = $"case {c.Number} {why}";

            ctx.Say($"[hazard #{ctx.Run.Id}] Case {c.Number} {(why == "done" ? "done" : "ended (" + why + ")")}." + (next < order.Count ? $" Next in {hold:0.#} s." : ""));
            ctx.Schedule(hold, () => RunNext(ctx));
        }

        /// <summary>Spawn, leg-end and marker point of a body: a wisp at the hazard height, a ball hovering ball_z above the ground.</summary>
        private Vector3 BodyPoint(HazardContext ctx, string bodyKind, Vector2 p)
            => bodyKind == HazardMoveGate.Wisp
                ? ctx.PointFor(p.X, p.Y, HazardFollow.HazardHeight)
                : ctx.PointFor(p.X, p.Y, HazardFollow.Ground, ballZ);

        /// <summary>Pure: the leg time at <paramref name="speed"/>, never under 0.5 s.</summary>
        internal static double LegSeconds(Vector2 from, Vector2 to, float speed)
            => Math.Max(0.5, Vector2.Distance(from, to) / Math.Max(0.1f, speed));

        private void LogSent(HazardContext ctx, int caseNumber, int leg, string msg, HazardHandle h)
        {
            var loc = h.Object?.Location;

            ctx.Log(FormattableString.Invariant(
                $"move-case={caseNumber} leg={leg} phase=sent msg={msg} hazard={h.Id}({h.Tag}) t={ctx.Now:0.000} at=({h.Rest.X:0.00},{h.Rest.Y:0.00},{h.Rest.Z:0.00}) server=({loc?.PositionX:0.00},{loc?.PositionY:0.00},{loc?.PositionZ:0.00}) cell=0x{loc?.Cell:X8}"));
        }

        private void LogPhase(HazardContext ctx, int caseNumber, int leg, string what, HazardHandle h, HazardPath path)
        {
            if (h == null || !h.Alive)
            {
                ctx.Log($"move-case={caseNumber} leg={leg} phase={what} hazard dead");
                return;
            }

            var now = ctx.Now;
            var computed = path != null ? path.PositionAt(now) : h.Rest;
            var predicted = path != null ? path.PredictedClientPosition(now) : h.Rest;
            var sentV = path?.SentVelocity ?? Vector3.Zero;
            var physV = h.Object.PhysicsObj?.Velocity ?? Vector3.Zero;
            var loc = h.Object.Location;
            var late = path != null ? now - path.EndTime : 0.0;

            if (loc == null)
            {
                ctx.Log(FormattableString.Invariant($"move-case={caseNumber} leg={leg} phase={what} hazard={h.Id}({h.Tag}) t={now:0.000} server=none"));
                return;
            }

            var server = loc.Pos;

            ctx.Log(FormattableString.Invariant(
                $"move-case={caseNumber} leg={leg} phase={what} hazard={h.Id}({h.Tag}) t={now:0.000} late={late:0.000} computed=({computed.X:0.00},{computed.Y:0.00},{computed.Z:0.00}) predicted=({predicted.X:0.00},{predicted.Y:0.00},{predicted.Z:0.00}) server=({server.X:0.00},{server.Y:0.00},{server.Z:0.00}) drift={Vector3.Distance(computed, server):0.00} cell=0x{loc.Cell:X8} lb=0x{loc.LandblockId.Landblock:X4} cur=0x{h.Object.CurrentLandblock?.Id.Landblock ?? 0:X4} sentv=({sentV.X:0.00},{sentV.Y:0.00},{sentV.Z:0.00}) physv=({physV.X:0.00},{physV.Y:0.00},{physV.Z:0.00})"));
        }

        public void Tick(HazardContext ctx)
        {
            if (!hit || body == null || !body.Alive)
                return;

            ctx.HitPlayers(body, body.PositionAt(ctx.Now), body.HitRadius, 3.0);
        }

        public void OnStop(HazardContext ctx, HazardOutcome outcome) => ctx.TraceTag = null;

        /// <summary>
        /// /hazard list: the phase and, for the hazard on screen, COMPUTED vs SERVER position, their distance
        /// ("drift") and the server object's landblock. See the class doc for what the drift should be.
        /// </summary>
        public string Describe(HazardContext ctx)
            => string.Format(CultureInfo.InvariantCulture, "{0}; {1}", phase, Readout(body, ctx.Now));

        internal static string Readout(HazardHandle h, double now)
        {
            if (h == null || !h.Alive)
                return "-";

            var computed = h.PositionAt(now);
            var loc = h.Object.Location;

            if (loc == null)
                return string.Format(CultureInfo.InvariantCulture, "{0} computed ({1:0.0}, {2:0.0}, {3:0.0}) server -", h.Tag, computed.X, computed.Y, computed.Z);

            var server = loc.Pos;

            return string.Format(CultureInfo.InvariantCulture,
                "{0} computed ({1:0.0}, {2:0.0}, {3:0.0}) server ({4:0.0}, {5:0.0}, {6:0.0}) drift {7:0.00} m lb 0x{8:X4} cur 0x{9:X4}{10}",
                h.Tag, computed.X, computed.Y, computed.Z, server.X, server.Y, server.Z, Vector3.Distance(computed, server),
                loc.LandblockId.Landblock, h.Object.CurrentLandblock?.Id.Landblock ?? 0, h.IsMoving ? " moving" : " at rest");
        }

        public string Reveal(HazardContext ctx) => null;
    }

    /// <summary>
    /// /hazard show - marks the region for "seconds" (default 10, 1..60): a fire pillar (or "script=") at each
    /// rect corner, or at 8 points round a circle's rim, plus the centre - every edge marker pulled edge_inset
    /// (default 2 m) inside the edge, so the markers show where effects will actually be placed. Effect hosts via
    /// ctx.SpawnTimedEffect.
    /// </summary>
    public sealed class HazardShowMechanic : IHazardMechanic
    {
        private double endsAt;
        private int markers;

        public string Kind => HazardMechanicRegistry.Show;

        public IReadOnlyList<string> Patterns => Array.Empty<string>();

        public IEnumerable<uint> RequiredWcids(HazardArgs args) => new[] { HazardDefaults.EffectHostWcid };

        public string Validate(HazardRegion region, HazardArgs args, string pattern) => null;

        public void Start(HazardContext ctx)
        {
            var seconds = ctx.Args.GetDouble("seconds", HazardDefaults.ShowSeconds, 1.0, 60.0);
            var script = HazardToolRoutes.ParseScript(ctx.Args.GetString("script"), HazardDefaults.PillarScript);

            foreach (var p in HazardToolRoutes.Outline(ctx.Region, ctx.EdgeInset))
            {
                if (ctx.SpawnTimedEffect(new Vector3(p, ctx.GroundZ(p.X, p.Y)), script, MemoryLayout.FloorOrientation(script), seconds, 1f, "marker", grounded: true) != null)
                    markers++;
            }

            endsAt = ctx.Now + seconds + 0.5;

            ctx.Say(FormattableString.Invariant(
                $"[hazard #{ctx.Run.Id}] showing {ctx.Region.Describe()} for {seconds:0.#} s ({markers} marker(s), edge markers {ctx.EdgeInset:0.#} m inside the edge)."));
        }

        public void Tick(HazardContext ctx)
        {
            if (ctx.Now >= endsAt)
                ctx.End(HazardOutcome.Completed, "show finished");
        }

        public void OnStop(HazardContext ctx, HazardOutcome outcome) { }

        public string Describe(HazardContext ctx) => $"{markers} marker(s) until t={endsAt:0.0}s";

        public string Reveal(HazardContext ctx) => null;
    }

    /// <summary>Pure geometry for /hazard show.</summary>
    public static class HazardToolRoutes
    {
        /// <summary>
        /// A rect's four corners or 8 points on a circle's rim, each pulled <paramref name="inset"/> metres inside
        /// the edge (HazardRegion.ClampInside), then the centre.
        /// </summary>
        public static IReadOnlyList<Vector2> Outline(HazardRegion region, float inset = 0f)
        {
            var points = new List<Vector2>();

            if (region.Shape == HazardShape.Rect)
            {
                points.Add(new Vector2(region.MinX, region.MinY));
                points.Add(new Vector2(region.MaxX, region.MinY));
                points.Add(new Vector2(region.MaxX, region.MaxY));
                points.Add(new Vector2(region.MinX, region.MaxY));
            }
            else
            {
                for (var i = 0; i < 8; i++)
                    points.Add(region.EdgePoint(i / 8.0, out _));
            }

            for (var i = 0; i < points.Count; i++)
                points[i] = region.ClampInside(points[i], inset);

            points.Add(region.Center);
            return points;
        }

        /// <summary>"0x33001028" or a decimal id; anything else -> <paramref name="defaultScript"/>.</summary>
        public static uint ParseScript(string text, uint defaultScript)
            => TryParseScript(text, out var script) ? script : defaultScript;

        /// <summary>A script id, decimal or 0x hex. False for blank or unparseable text (<paramref name="script"/> is then 0).</summary>
        public static bool TryParseScript(string text, out uint script)
        {
            script = 0;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            text = text.Trim();

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out script);

            return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out script);
        }
    }

    /// <summary>One straight line the movement gate can use, with where it enters and leaves a solid (if it crosses one).</summary>
    public sealed class MoveGateSegment
    {
        public MoveGateSegment(Vector2 from, Vector2 to, Vector2? solidEnter = null, Vector2? solidExit = null)
        {
            From = from;
            To = to;
            SolidEnter = solidEnter;
            SolidExit = solidExit;
        }

        public Vector2 From { get; }
        public Vector2 To { get; }
        public Vector2? SolidEnter { get; }
        public Vector2? SolidExit { get; }
        public bool Crossing => SolidEnter.HasValue;
        public float Length => Vector2.Distance(From, To);
    }

    /// <summary>One movement-gate case: which body, its points (element 0 = spawn, i = leg i's target), or why it is skipped.</summary>
    public sealed class MoveGateCase
    {
        public int Number { get; set; }
        public string Body { get; set; }
        public string Title { get; set; }
        public IReadOnlyList<Vector2> Points { get; set; } = Array.Empty<Vector2>();
        public MoveGateSegment Segment { get; set; }
        public string Skip { get; set; }

        public int LegCount => Points.Count > 0 ? Points.Count - 1 : 0;

        public string Describe()
        {
            if (Skip != null)
                return $"{Number}:{Body} skipped";

            var parts = new List<string>();

            foreach (var p in Points)
                parts.Add(FormattableString.Invariant($"({p.X:0.0},{p.Y:0.0})"));

            return $"{Number}:{Body} {string.Join("->", parts)}";
        }
    }

    /// <summary>
    /// PURE planner for /hazard move v2. Lines are axis-aligned and sampled on a <c>step</c> grid (multiples of the
    /// step, so the same plan comes out every time), kept <c>inset</c> inside the region edge. "Solid" is a
    /// passed-in predicate (live: no cell at the hazard height), so the whole plan is testable without a world.
    /// </summary>
    public static class HazardMoveGate
    {
        public const int CaseCount = 5;

        public const string Wisp = "wisp";
        public const string Ball = "ball";

        public const string NoOpenLine = "no straight open line of at least 6 m with 2.5 m of open space either side fits in this region";
        public const string NoCrossingLine = "no straight line in this region crosses a solid (every sampled point has a cell at the hazard height)";

        /// <summary>The multiples of <paramref name="step"/> in [min, max].</summary>
        public static IReadOnlyList<float> Grid(float min, float max, float step)
        {
            var list = new List<float>();

            if (!(step > 0f) || !float.IsFinite(min) || !float.IsFinite(max))
                return list;

            var a = (long)Math.Ceiling(min / (double)step - 1e-4);
            var b = (long)Math.Floor(max / (double)step + 1e-4);

            for (var i = a; i <= b; i++)
                list.Add((float)(i * (double)step));

            return list;
        }

        /// <summary>Inside the shape, outside every exclusion, and at least <paramref name="inset"/> from the edge.</summary>
        public static bool Usable(HazardRegion region, Vector2 p, float inset)
            => region.IsPlaceable(p.X, p.Y) && region.DistanceToEdge(p.X, p.Y) >= inset - 1e-3f;

        private static IEnumerable<(bool Rows, List<Vector2> Points)> Lines(HazardRegion region, float inset, float step, bool centreOut)
        {
            foreach (var rows in new[] { true, false })
            {
                var fixedValues = rows ? Grid(region.MinY + inset, region.MaxY - inset, step) : Grid(region.MinX + inset, region.MaxX - inset, step);
                var along = rows ? Grid(region.MinX + inset, region.MaxX - inset, step) : Grid(region.MinY + inset, region.MaxY - inset, step);

                var orderedFixed = new List<float>(fixedValues);

                if (centreOut)
                {
                    var c = rows ? region.CenterY : region.CenterX;
                    orderedFixed.Sort((p, q) => Math.Abs(p - c) != Math.Abs(q - c) ? Math.Abs(p - c).CompareTo(Math.Abs(q - c)) : p.CompareTo(q));
                }

                foreach (var f in orderedFixed)
                {
                    var points = new List<Vector2>(along.Count);

                    foreach (var a in along)
                        points.Add(rows ? new Vector2(a, f) : new Vector2(f, a));

                    yield return (rows, points);
                }
            }
        }

        /// <summary>
        /// The OPEN line: on rows (west-east), else columns (south-north), the longest run of usable points where
        /// the point and the points <paramref name="margin"/> either side of it are all open, capped at
        /// <paramref name="maxLength"/> and centred on the run. Of the lines that reach the best length, the middle
        /// line of the widest consecutive band of them is taken (furthest from whatever bounds the band). Null when
        /// no run reaches <paramref name="minLength"/>.
        /// </summary>
        public static MoveGateSegment FindOpenSegment(HazardRegion region, float inset, Func<Vector2, bool> solid,
            float margin = 2.5f, float minLength = 6f, float maxLength = 20f, float step = 0.5f)
        {
            if (region == null || solid == null || !(step > 0f))
                return null;

            var minSamples = (int)Math.Ceiling(minLength / step - 1e-4);
            var maxSamples = (int)Math.Floor(maxLength / step + 1e-4);

            MoveGateSegment best = null;
            var bestScore = -1;

            foreach (var rows in new[] { true, false })
            {
                var lateral = rows ? new Vector2(0f, margin) : new Vector2(margin, 0f);
                var lines = new List<(List<Vector2> Points, int Start, int Len, int Score)>();

                foreach (var (r, points) in Lines(region, inset, step, centreOut: false))
                {
                    if (r != rows)
                        continue;

                    var (start, len) = LongestRun(points, p => Usable(region, p, inset) && !solid(p) && !solid(p + lateral) && !solid(p - lateral));
                    var span = len - 1;
                    lines.Add((points, start, len, span >= minSamples ? Math.Min(span, maxSamples) : -1));
                }

                var score = -1;

                foreach (var l in lines)
                    score = Math.Max(score, l.Score);

                if (score < 0 || score <= bestScore)
                    continue;

                // the widest band of consecutive lines at that score; its middle line
                int bandStart = -1, bandLen = 0;

                for (var i = 0; i < lines.Count;)
                {
                    if (lines[i].Score != score)
                    {
                        i++;
                        continue;
                    }

                    var j = i;

                    while (j < lines.Count && lines[j].Score == score)
                        j++;

                    if (j - i > bandLen)
                    {
                        bandStart = i;
                        bandLen = j - i;
                    }

                    i = j;
                }

                var line = lines[bandStart + (bandLen - 1) / 2];
                var first = line.Start;
                var last = line.Start + line.Len - 1;
                var mid = (first + last) / 2;
                var from = Math.Max(first, mid - score / 2);
                var to = Math.Min(last, from + score);

                best = new MoveGateSegment(line.Points[from], line.Points[to]);
                bestScore = score;
            }

            return best;
        }

        /// <summary>
        /// The CROSSING line: lines tried from the region's centre outward (rows first), the first with an open run
        /// of at least <paramref name="minOpen"/>, then a solid run, then another open run of at least
        /// <paramref name="minOpen"/>. The segment starts <paramref name="approach"/> before the solid and ends
        /// <paramref name="approach"/> past it (cut to the open runs). Null when no line crosses a solid.
        /// </summary>
        public static MoveGateSegment FindCrossingSegment(HazardRegion region, float inset, Func<Vector2, bool> solid,
            float approach = 3f, float minOpen = 2f, float step = 0.5f)
        {
            if (region == null || solid == null || !(step > 0f))
                return null;

            var k = Math.Max(1, (int)Math.Round(approach / step));
            var minOpenSamples = (int)Math.Ceiling(minOpen / step - 1e-4);

            foreach (var (_, points) in Lines(region, inset, step, centreOut: true))
            {
                // 0 unusable, 1 open, 2 solid
                var runs = new List<(int State, int Start, int End)>();

                for (var i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    var state = !Usable(region, p, inset) ? 0 : solid(p) ? 2 : 1;

                    if (runs.Count > 0 && runs[runs.Count - 1].State == state && runs[runs.Count - 1].End == i - 1)
                        runs[runs.Count - 1] = (state, runs[runs.Count - 1].Start, i);
                    else
                        runs.Add((state, i, i));
                }

                for (var r = 0; r + 2 < runs.Count; r++)
                {
                    var a = runs[r];
                    var s = runs[r + 1];
                    var b = runs[r + 2];

                    if (a.State != 1 || s.State != 2 || b.State != 1)
                        continue;

                    if (a.End - a.Start < minOpenSamples || b.End - b.Start < minOpenSamples)
                        continue;

                    var from = Math.Max(a.Start, a.End - k);
                    var to = Math.Min(b.End, b.Start + k);

                    return new MoveGateSegment(points[from], points[to],
                        (points[a.End] + points[s.Start]) * 0.5f, (points[s.End] + points[b.Start]) * 0.5f);
                }
            }

            return null;
        }

        private static (int Start, int Len) LongestRun(List<Vector2> points, Func<Vector2, bool> ok)
        {
            int bestStart = 0, bestLen = 0, start = -1;

            for (var i = 0; i <= points.Count; i++)
            {
                var good = i < points.Count && ok(points[i]);

                if (good && start < 0)
                    start = i;

                if (!good && start >= 0)
                {
                    if (i - start > bestLen)
                    {
                        bestStart = start;
                        bestLen = i - start;
                    }

                    start = -1;
                }
            }

            return (bestStart, bestLen);
        }

        /// <summary>The five cases, numbered 1..5 in order. A case the region cannot supply carries a Skip reason.</summary>
        public static IReadOnlyList<MoveGateCase> Plan(HazardRegion region, float inset, Func<Vector2, bool> solid, float maxLength = 20f)
        {
            var open = FindOpenSegment(region, inset, solid, maxLength: maxLength);
            var cross = FindCrossingSegment(region, inset, solid);

            MoveGateCase Make(int n, string bodyKind, string title, MoveGateSegment seg, string skip, bool backAndForth = false, bool intoSolid = false)
                => seg == null
                    ? new MoveGateCase { Number = n, Body = bodyKind, Title = title, Skip = skip }
                    : new MoveGateCase
                    {
                        Number = n,
                        Body = bodyKind,
                        Title = title,
                        Segment = seg,
                        Points = backAndForth ? new[] { seg.From, seg.To, seg.From }
                            : intoSolid ? new[] { seg.From, (seg.SolidEnter.Value + seg.SolidExit.Value) * 0.5f }
                            : new[] { seg.From, seg.To },
                    };

            return new[]
            {
                Make(1, Wisp, "wisp, open air", open, NoOpenLine),
                Make(2, Ball, "ball, open air", open, NoOpenLine),
                Make(3, Ball, "ball, across a solid top", cross, NoCrossingLine),
                Make(4, Wisp, "wisp, towards the middle of a solid", cross, NoCrossingLine, intoSolid: true),
                Make(5, Ball, "ball, out and back with no pause", open, NoOpenLine, backAndForth: true),
            };
        }

        /// <summary>EAST (+x), WEST, NORTH (+y), SOUTH, by the larger component.</summary>
        public static string Heading(Vector2 from, Vector2 to)
        {
            var d = to - from;

            if (Math.Abs(d.X) >= Math.Abs(d.Y))
                return d.X >= 0f ? "EAST" : "WEST";

            return d.Y >= 0f ? "NORTH" : "SOUTH";
        }

        /// <summary>The chat line announcing <paramref name="c"/> before it runs. Pure.</summary>
        /// <remarks><paramref name="pillar"/>: case 4's true target when the wisp was stopped short of it (HazardGround.PlaceWisp).</remarks>
        public static string Announce(MoveGateCase c, IReadOnlyList<Vector3> points, float speed, IReadOnlyList<double> durations, Vector3? pillar = null)
        {
            string P(Vector3 p) => FormattableString.Invariant($"({p.X:0.0}, {p.Y:0.0})");

            var head = FormattableString.Invariant($"Case {c.Number}/{CaseCount} - ");
            var a = points[0];
            var b = points[1];
            var dir = Heading(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
            var timing = FormattableString.Invariant($"at z {a.Z:0.00}, {speed:0.0} m/s, {durations[0]:0.0} s");
            var seg = c.Segment;

            switch (c.Number)
            {
                case 1:
                    return head + $"WISP flies {dir} through OPEN air only (no solid on or near its line) from {P(a)} to {P(b)} {timing}. Pillars mark both ends. EXPECT: a straight glide at constant height that stops ON the far pillar with no jump.";

                case 2:
                    return head + $"BALL rolls {dir} over OPEN air only (nothing solid under or beside its line) from {P(a)} to {P(b)} {timing}. Pillars mark both ends. EXPECT: a straight roll that stops ON the far pillar with no jump and no sideways slide.";

                case 3:
                    return head + $"BALL rolls {dir} ACROSS a solid top (solid from {P2(seg.SolidEnter)} to {P2(seg.SolidExit)}) from {P(a)} to {P(b)} {timing}, HOVERING above the floor so it clears the top. EXPECT: a straight roll over the solid to the far pillar. A stop, a sideways shove or a pop-up at the solid's edge means the hover is too low.";

                case 4:
                    return head + (pillar.HasValue
                        ? FormattableString.Invariant($"WISP flies {dir} towards {P(pillar.Value)} INSIDE a solid (solid starts at {P2(seg.SolidEnter)}). Wisps never enter solids: it must STOP short of the solid at {P(b)} {timing}. A third pillar fires at the true target on the solid's top (z {pillar.Value.Z:0.00}). EXPECT: a clean stop on the near pillar, never inside the solid.")
                        : $"WISP flies {dir} from {P(a)} to {P(b)} {timing}; no solid was in its way. EXPECT: a straight glide that stops ON the far pillar.");

                default:
                    return head + FormattableString.Invariant(
                        $"BALL rolls {dir} from {P(a)} to {P(b)} then straight BACK to {P(a)} with NO pause, at z {a.Z:0.00}, {speed:0.0} m/s, {durations[0]:0.0} s + {durations[Math.Min(1, durations.Count - 1)]:0.0} s. EXPECT: one smooth turnaround at the far pillar. A jump or blink at the turn means the leg-end snap disagrees with where your client has it.");
            }

            static string P2(Vector2? p) => p.HasValue ? FormattableString.Invariant($"({p.Value.X:0.0}, {p.Value.Y:0.0})") : "?";
        }
    }
}
