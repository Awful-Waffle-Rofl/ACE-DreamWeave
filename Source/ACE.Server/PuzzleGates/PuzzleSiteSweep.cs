using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

using ACE.Server.ThreadDungeons;

namespace ACE.Server.PuzzleGates
{
    /// <summary>One object of a swept placement as it stood after the real spawn (PuzzleSiteSweepRunner reads it on the landblock thread).</summary>
    public sealed class SweepObject
    {
        /// <summary>Gate for a gate part (door, barrier panel, focal); otherwise the plan spec's role.</summary>
        public PuzzleRole Role { get; init; }

        /// <summary>The spec's slot (lever slot, beam index j, indicator index), or the gate part index.</summary>
        public int Slot { get; init; }

        /// <summary>Where the plan put it (landblock-local).</summary>
        public Vector3 Planned { get; init; }

        /// <summary>The cell the server resolves for the planned point (PuzzleGateManager.TryResolve); 0 = none.</summary>
        public uint ExpectedCell { get; init; }

        /// <summary>In the world: created, not destroyed, and the physics object holds a cell.</summary>
        public bool Entered { get; init; }

        /// <summary>The object's final Location cell; 0 when it did not enter.</summary>
        public uint Cell { get; init; }

        /// <summary>The object's final Location position (after the physics placement).</summary>
        public Vector3? Final { get; init; }

        public Quaternion? FinalRotation { get; init; }

        /// <summary>
        /// Whether the planned point lies inside the object's own physics cell (EnvCell.point_in_cell), at the floor
        /// point or at the object's body height (<see cref="PuzzleSiteSweep.BodyLift"/>). Null = not known (not
        /// entered, or an outdoor cell). Cells overlap at a boundary, and AdjustCell reports only the lowest-numbered
        /// cell holding a point, so a different <see cref="ExpectedCell"/> is not by itself a mismatch.
        /// </summary>
        public bool? PlannedInCell { get; init; }

        /// <summary>The same question for the object's final (spawned) position. Counts only when check b accepts its drift.</summary>
        public bool? FinalInCell { get; init; }
    }

    /// <summary>A lever point no lever stands on in the swept round (another shuffle spot), with the cell the server resolves for it.</summary>
    public readonly record struct SweepPoint(int Slot, Vector3 Point, uint Cell);

    /// <summary>Everything one swept placement recorded: the inputs, and what the real placement produced. Pure data.</summary>
    public sealed record SweepRecord
    {
        public string Dungeon { get; init; }
        public string SiteId { get; init; }

        /// <summary>"gate" or "reward": the site's kind in puzzle-gates.json.</summary>
        public string SiteKind { get; init; }

        /// <summary>"gate" (the site's own door or barrier) or "reward" (the focal object, as the reward scene would stand).</summary>
        public string Role { get; init; }

        public PuzzleGateType Type { get; init; }
        public int N { get; init; }

        /// <summary>The site's kill-switch state when swept: "", "disabled" or "reward-disabled".</summary>
        public string SiteState { get; init; } = "";

        /// <summary>Non-null: the placement was not attempted (a resident-door site, options refused); the row is unchecked.</summary>
        public string SkipReason { get; init; }

        /// <summary>Non-null: the placement was refused, or its spawn failed or never ran.</summary>
        public string SpawnError { get; init; }

        public int PlannedGateParts { get; init; }
        public int PlannedRoundObjects { get; init; }

        public IReadOnlyList<SweepObject> Objects { get; init; } = Array.Empty<SweepObject>();
        public IReadOnlyList<SweepPoint> ExtraLeverPoints { get; init; } = Array.Empty<SweepPoint>();

        public IReadOnlyList<int> BeamTargets { get; init; }
        public PuzzleBeamAxis Axis { get; init; }

        public uint AnchorCell { get; init; }
        public Vector3 Anchor { get; init; }

        /// <summary>Where the gate stands (ThreadPuzzlePass.GatePoint): the point whose doorway a gate role blocks.</summary>
        public Vector3 GatePoint { get; init; }

        /// <summary>Check c's walk probe (PuzzleWalkProbe): every leg walked, out and back. Null = the probe did not run (<see cref="ProbeNote"/> says why).</summary>
        public IReadOnlyList<ProbeLeg> Probe { get; init; }

        public string ProbeNote { get; init; }

        /// <summary>Wall-clock time of the row: place, wait for the spawn, snapshot and probe, clear.</summary>
        public long ElapsedMs { get; init; }

        /// <summary>Landblock-thread time the walk probe took (the sum of its per-leg actions).</summary>
        public double ProbeMs { get; init; }

        /// <summary>How far from the curated anchor the probe found standing room for its start (0 = on the anchor).</summary>
        public float ProbeStartOffset { get; init; }

        /// <summary>How the probe chose the anchor's region cell when the anchor itself is in no cell (null = the anchor's own cell).</summary>
        public string ProbeStartNote { get; init; }

        /// <summary>Landblock-thread time of the start search (region, reachability, FindStart).</summary>
        public double StartMs { get; init; }

        /// <summary>The longest single probe action of the row (one landblock action), ms.</summary>
        public double MaxActionMs { get; init; }

        /// <summary>The placement seed (0 = not placed); reproduce a row with /puzzlegate site ... seed=.</summary>
        public int Seed { get; init; }
    }

    public enum ProbeOutcome
    {
        Pass,
        Blocked,
        Unchecked,
    }

    /// <summary>
    /// One leg of the walk probe: <see cref="Direction"/> "out" (anchor to target) or "back" (target to anchor). Blocked
    /// carries where the body stopped (<see cref="At"/>) and why; Path says "straight" or "via N doorway(s)".
    /// </summary>
    public sealed record ProbeLeg(string Target, string Direction, ProbeOutcome Outcome, Vector3? At, string Reason)
    {
        public string Path { get; init; }

        /// <summary>Openable retail doors (not puzzle gate parts) the body walked through on this leg, as "wcid N". Null = none.</summary>
        public IReadOnlyList<string> DoorsPassed { get; init; }

        /// <summary>What the arrival rule measured at the leg's end (a passing leg under a use-range or return rule).</summary>
        public string Measure { get; init; }

        public static ProbeLeg Blocked(string target, string direction, Vector3 at, string reason) => new ProbeLeg(target, direction, ProbeOutcome.Blocked, at, reason);

        public static ProbeLeg Unchecked(string target, string direction, string reason) => new ProbeLeg(target, direction, ProbeOutcome.Unchecked, null, reason);

        public static ProbeLeg Passed(Vector3 at) => new ProbeLeg(null, null, ProbeOutcome.Pass, at, null);

        public string Describe()
        {
            var inv = CultureInfo.InvariantCulture;

            switch (Outcome)
            {
                case ProbeOutcome.Pass:
                    return $"{Target} {Direction}: pass{(DoorsPassed == null ? "" : " through door " + string.Join(", ", DoorsPassed))}";
                case ProbeOutcome.Blocked:
                    return string.Format(inv, "{0} {1}: blocked-at ({2:0.0}, {3:0.0}, {4:0.0}) {5}{6}", Target, Direction,
                        At?.X ?? 0f, At?.Y ?? 0f, At?.Z ?? 0f, Reason, Path == null ? "" : $" [{Path}]");
                default:
                    return $"{Target} {Direction}: unchecked {Reason}";
            }
        }
    }

    /// <summary>A point the walk probe must reach from the anchor and leave again: a lever or a shuffle spot (the focal approach only when a caller passes one).</summary>
    public readonly record struct ProbeTarget(string Label, uint Cell, Vector3 Point)
    {
        /// <summary>The object a player uses at this target (its cylinder and UseRadius). Null = the old point rule.</summary>
        public UseReach? Reach { get; init; }
    }

    /// <summary>
    /// A used object's cylinder (PhysicsObj radius and height) and its use range (WorldObject.UseRadius, or the
    /// server's 0.6 m default when unset), as WorldObject.IsWithinUseRadiusOf measures it.
    /// </summary>
    public readonly record struct UseReach(float Radius, float Height, float UseRadius, string Source);

    /// <summary>One row's verdict: result pass|fail|unchecked, the first failing check (a-e, or "-"), and the detail.</summary>
    public sealed record SweepOutcome(string Result, string FailingCheck, string Detail);

    /// <summary>
    /// puzzlegate-sweep's pure half: the checks over a recorded placement, the TSV row, and the summary. The live half
    /// (PuzzleSiteSweepRunner) places through the real PuzzleGateManager.PlaceForRun into a throwaway copy and records.
    ///
    /// Checks, in order (the first failing one names the row):
    /// <list type="bullet">
    /// <item>a - every planned object entered the world: the spawn ran and did not fail, the gate part and round object
    /// counts match the plan, and each object holds a physics cell.</item>
    /// <item>b - each lever's final Location is within <see cref="MaxDrift"/> of its planned point, horizontally and
    /// vertically (a placement that collides is slid aside). Beyond <see cref="MaxHorizontalDrift"/> across or
    /// <see cref="MaxVerticalDrift"/> up/down but within that limit, the drift fails only when the walk probe to that
    /// lever (which targets the lever where it actually stands) is blocked; otherwise it is a note in the detail.</item>
    /// <item>c - the server resolves a cell for every lever point (spawned or a further shuffle spot), a spawned lever's
    /// physics cell contains its planned point or, when b accepts its drift, where it stands (floor or body height; see SweepObject.PlannedInCell / FinalInCell), and the
    /// WALK PROBE (PuzzleWalkProbe): a player-sized body walks, through the server's own collision geometry with the gate
    /// parts standing, from the nearest standing point within PuzzleWalkProbe.StartRadius (1.5 m) of the anchor to every
    /// target (each lever where it stands, each further shuffle spot) until the server's use check passes (UseArrival),
    /// and back to within 1.0 m of that start (ReturnArrival), on a walkable floor,
    /// and for a gate role never into a cell beyond the gate. A probe that could not run leaves c unchecked.</item>
    /// <item>d - over the dungeon's walk graph (DungeonWalkGraph) from the anchor cell: every lever point's cell is
    /// reachable. A GATE role blocks the doorway the gate stands in (DungeonWalkGraph.NearestDoorway) first, so the
    /// levers must be on the near side; and the doorway's far cell must then NOT be reachable, which proves the gate
    /// closes the way. No graph: d is unchecked.</item>
    /// <item>e - beam puzzles: each beam host's final orientation points at the lever the plan's BeamTargets names (the
    /// lever whose beam target point lies nearest the host's beam ray).</item>
    /// </list>
    /// </summary>
    public static class PuzzleSiteSweep
    {
        public const float MaxHorizontalDrift = 0.25f;
        public const float MaxVerticalDrift = 0.5f;

        /// <summary>Hard drift limit (3D distance): beyond it check b fails whatever the walk probe says.</summary>
        public const float MaxDrift = 1.0f;

        /// <summary>
        /// A lever counts as usable only this far INSIDE its UseRadius: the probe's cylinder distance is measured from a
        /// body that stops the moment the check passes, and a player's position drifts a little; a lever reachable only
        /// at the very edge of its range is not one a player reliably uses.
        /// </summary>
        public const float UseMargin = 0.15f;

        /// <summary>
        /// A gate row's region cell must be on the approach side: when the region is a different cell from the site's
        /// declared anchor cell, the walk graph must reach the region from the anchor cell with the gate doorway closed.
        /// No graph, or an anchor cell the graph does not hold, proves nothing: that is a reason too, never a pass.
        /// Null = proven (or nothing to prove: a reward row, or the region IS the anchor cell); otherwise the reason for
        /// an unchecked start. Pure.
        /// </summary>
        public static string RegionNotProven(DungeonWalkGraph graph, bool gateRole, uint anchorCell, Vector3 anchor, uint region, ICollection<(uint, uint)> blocked)
        {
            if (!gateRole || region == anchorCell)
                return null;

            if (graph == null)
                return string.Format(CultureInfo.InvariantCulture, "region 0x{0:X8} not proven on the approach side: no walk graph to reach it from the anchor cell 0x{1:X8}", region, anchorCell);

            if (!graph.HasCell(anchorCell))
                return string.Format(CultureInfo.InvariantCulture, "region 0x{0:X8} not proven on the approach side: the anchor cell 0x{1:X8} is not in the walk graph", region, anchorCell);

            var field = graph.From(anchorCell, anchor, blocked);

            return field != null && field.Reaches(region)
                ? null
                : string.Format(CultureInfo.InvariantCulture, "region 0x{0:X8} not proven on the approach side (the walk graph does not reach it from the anchor cell 0x{1:X8} with the gate doorway closed)", region, anchorCell);
        }

        /// <summary>
        /// The cells a gate row's legs may enter: what the walk graph reaches from the region with the gate doorway
        /// closed; when the region is not in the graph, from the declared anchor cell instead. Cells outside the graph
        /// stay allowed (the graph has nothing to say about them). Null with <paramref name="why"/> when neither field
        /// exists: the gate cannot be checked, and the row's start is unchecked. Pure.
        /// </summary>
        public static Func<uint, bool> GateField(DungeonWalkGraph graph, uint region, Vector3 regionPoint, uint anchorCell, Vector3 anchor, ICollection<(uint, uint)> blocked, out string why)
        {
            why = null;
            var field = graph.HasCell(region) ? graph.From(region, regionPoint, blocked) : null;

            if (field == null && graph.HasCell(anchorCell))
                field = graph.From(anchorCell, anchor, blocked);

            if (field == null)
            {
                why = string.Format(CultureInfo.InvariantCulture, "no walk field for the gate check: neither the region 0x{0:X8} nor the anchor cell 0x{1:X8} is in the walk graph", region, anchorCell);
                return null;
            }

            return cell => !graph.HasCell(cell) || field.Reaches(cell);
        }

        /// <summary>
        /// The gate decision for one row's probe, in one place so the runner cannot wire it differently: the cells the
        /// legs may enter (<see cref="GateGuard.Allowed"/>, null = unrestricted) or why the row cannot be checked
        /// (<see cref="GateGuard.UncheckedReason"/>). A reward row is unrestricted. A gate row needs a gate doorway
        /// (<paramref name="blocked"/>, from the walk graph's nearest doorway to the gate): without one its legs would
        /// run with nothing closed, so it is unchecked. Then its region must be proven on the approach side
        /// (<see cref="RegionNotProven"/>) and a gate field must exist (<see cref="GateField"/>). Pure.
        /// </summary>
        public static GateGuard GateGuardFor(DungeonWalkGraph graph, bool gateRole, uint anchorCell, Vector3 anchor, uint region, Vector3 regionPoint, ICollection<(uint, uint)> blocked)
        {
            if (!gateRole)
                return new GateGuard(null, null);

            if (graph == null || blocked == null || blocked.Count == 0)
                return new GateGuard(null, graph == null
                    ? "no walk graph: the gate doorway cannot be closed, so the gate cannot be checked"
                    : "no gate doorway found in the walk graph near the gate: the gate cannot be closed, so it cannot be checked");

            var notProven = RegionNotProven(graph, true, anchorCell, anchor, region, blocked);

            if (notProven != null)
                return new GateGuard(null, notProven);

            var allowed = GateField(graph, region, regionPoint, anchorCell, anchor, blocked, out var why);
            return allowed == null ? new GateGuard(null, why) : new GateGuard(allowed, null);
        }

        /// <summary>The cells a row's legs may enter (null = unrestricted), or why the row is unchecked (then Allowed is null).</summary>
        public readonly record struct GateGuard(Func<uint, bool> Allowed, string UncheckedReason);

        /// <summary>
        /// The cells a probe start may stand in: <paramref name="regionCell"/> itself, or a cell the walk graph reaches
        /// from it with <paramref name="blocked"/> (a gate role's doorway) closed. Null = no proof available (no graph,
        /// or the region cell is not in it): a start outside the region cell is then unchecked, never passed. Pure.
        /// </summary>
        public static Func<uint, bool> StartReach(DungeonWalkGraph graph, uint regionCell, Vector3 regionPoint, ICollection<(uint, uint)> blocked)
        {
            if (graph == null || regionCell == 0 || !graph.HasCell(regionCell))
                return null;

            var field = graph.From(regionCell, regionPoint, blocked);

            if (field == null)
                return null;

            return c => c == regionCell || (graph.HasCell(c) && field.Reaches(c));
        }

        /// <summary>How far above a floor point an object's body sits: half its physics height (0.5 m when it has none).</summary>
        public static float BodyLift(float height) => height > 0f ? height / 2f : 0.5f;

        public const string Pass = "pass";
        public const string Fail = "fail";
        public const string Unchecked = "unchecked";

        public static SweepOutcome Evaluate(SweepRecord r, DungeonWalkGraph graph)
        {
            if (r.SkipReason != null)
                return new SweepOutcome(Unchecked, "-", r.SkipReason);

            var failures = new List<(string Check, string Detail)>();
            var uncheckedNotes = new List<(string Check, string Note)>();
            var info = new List<string>();
            var inv = CultureInfo.InvariantCulture;

            // ---- a: entered ----
            if (r.SpawnError != null)
            {
                failures.Add(("a", "spawn: " + r.SpawnError));
                return Verdict(failures, uncheckedNotes, info);
            }

            var gateParts = r.Objects.Where(o => o.Role == PuzzleRole.Gate).ToList();
            var round = r.Objects.Where(o => o.Role != PuzzleRole.Gate).ToList();

            if (gateParts.Count != r.PlannedGateParts)
                failures.Add(("a", $"gate parts {gateParts.Count}/{r.PlannedGateParts}"));

            if (round.Count != r.PlannedRoundObjects)
                failures.Add(("a", $"round objects {round.Count}/{r.PlannedRoundObjects}"));

            foreach (var o in r.Objects.Where(o => !o.Entered))
                failures.Add(("a", $"{Name(o)} not in the world"));

            var levers = round.Where(o => o.Role == PuzzleRole.Candidate && o.Entered && o.Final.HasValue).OrderBy(o => o.Slot).ToList();

            // ---- b: not ejected ----
            var driftAccepted = new HashSet<int>();

            foreach (var l in levers)
            {
                var d = l.Final.Value - l.Planned;
                var h = new Vector2(d.X, d.Y).Length();

                if (h <= MaxHorizontalDrift && Math.Abs(d.Z) <= MaxVerticalDrift)
                {
                    driftAccepted.Add(l.Slot);
                    continue;
                }

                var moved = string.Format(inv, "{0} moved {1:0.00} m across, {2:0.00} m up/down from its planned point", Name(l), h, d.Z);

                if (d.Length() > MaxDrift)
                {
                    failures.Add(("b", moved + string.Format(inv, " ({0:0.00} m in 3D, limit {1:0.0} m)", d.Length(), MaxDrift)));
                    continue;
                }

                // Within the hard limit: the lever counts where it stands if a player can still walk to it and use it.
                var legs = r.Probe?.Where(x => string.Equals(x.Target, Name(l), StringComparison.Ordinal)).ToList();

                if (legs == null || legs.Count == 0 || legs.Any(x => x.Outcome == ProbeOutcome.Unchecked))
                    uncheckedNotes.Add(("b", "b: " + moved + ", and the walk probe to it did not run"));
                else if (legs.Any(x => x.Outcome == ProbeOutcome.Blocked))
                    failures.Add(("b", moved + ", and the walk probe to it is blocked"));
                else
                {
                    driftAccepted.Add(l.Slot);
                    info.Add(moved + " (walk probe reaches it)");
                }
            }

            // ---- c: a cell under every lever point ----
            foreach (var l in round.Where(o => o.Role == PuzzleRole.Candidate))
            {
                if (l.ExpectedCell == 0)
                    failures.Add(("c", $"{Name(l)}: no cell at its planned point"));
                else if (l.Entered && l.Cell != l.ExpectedCell)
                {
                    if (l.PlannedInCell == true)
                        info.Add($"{Name(l)}: physics cell 0x{l.Cell:X8} also contains its planned point (overlaps 0x{l.ExpectedCell:X8})");
                    else if (l.FinalInCell == true && driftAccepted.Contains(l.Slot))
                        info.Add($"{Name(l)}: physics cell 0x{l.Cell:X8} contains where it stands (drift accepted; planned point resolves to 0x{l.ExpectedCell:X8})");
                    else
                        failures.Add(("c", $"{Name(l)}: physics cell 0x{l.Cell:X8} contains neither its planned point nor where it stands (accepted drift), planned point resolves to 0x{l.ExpectedCell:X8}"));
                }
            }

            foreach (var p in r.ExtraLeverPoints.Where(p => p.Cell == 0))
                failures.Add(("c", $"shuffle spot {p.Slot + 1}: no cell at its point"));

            // ---- c: the walk probe ----
            if (r.Probe == null)
            {
                uncheckedNotes.Add(("c", "c: walk probe not run" + (string.IsNullOrEmpty(r.ProbeNote) ? "" : $" ({r.ProbeNote})")));
            }
            else
            {
                foreach (var leg in r.Probe.Where(l => l.Outcome == ProbeOutcome.Blocked))
                    failures.Add(("c", "probe " + leg.Describe()));

                var skipped = r.Probe.Where(l => l.Outcome == ProbeOutcome.Unchecked).ToList();

                var doors = DoorsPassedNote(r.Probe);

                if (doors != null)
                    info.Add(doors);

                if (r.ProbeStartOffset > 0f)
                    info.Add(string.Format(inv, "probe start {0:0.00} m from the anchor (the anchor itself has no standing room)", r.ProbeStartOffset));

                if (r.ProbeStartNote != null)
                    info.Add(r.ProbeStartNote);

                if (skipped.Count > 0)
                    uncheckedNotes.Add(("c", "c: " + string.Join("; ", skipped.Select(l => l.Describe()))));
            }

            // ---- d: reachable on the approach side, and the gate closes the way ----
            if (graph == null)
            {
                uncheckedNotes.Add(("d", "d: no walk graph for the dungeon"));
            }
            else
            {
                HashSet<(uint, uint)> blocked = null;
                (uint, uint)? doorway = null;
                var isGate = string.Equals(r.Role, "gate", StringComparison.Ordinal);

                if (isGate)
                {
                    doorway = graph.NearestDoorway(r.GatePoint);

                    if (doorway == null)
                        failures.Add(("d", "no doorway portal at the gate point (the gate blocks nothing the walk knows)"));
                    else
                        blocked = new HashSet<(uint, uint)> { doorway.Value };
                }

                var field = graph.From(r.AnchorCell, r.Anchor, blocked);

                if (field == null)
                {
                    failures.Add(("d", $"anchor cell 0x{r.AnchorCell:X8} is not in the walk graph"));
                }
                else
                {
                    var points = levers.Select(l => (Label: Name(l), Cell: l.Cell, Point: l.Final.Value))
                        .Concat(r.ExtraLeverPoints.Where(p => p.Cell != 0).Select(p => (Label: $"shuffle spot {p.Slot + 1}", Cell: p.Cell, Point: p.Point)));

                    foreach (var (label, cell, point) in points)
                    {
                        if (field.DistanceTo(cell, point) == null)
                            failures.Add(("d", $"{label} in 0x{cell:X8} is not reachable from the anchor{(isGate ? " without passing the gate" : "")}"));
                    }

                    if (isGate && doorway.HasValue)
                    {
                        var (c1, c2) = doorway.Value;
                        var r1 = field.DistanceTo(c1, r.GatePoint) != null;
                        var r2 = field.DistanceTo(c2, r.GatePoint) != null;

                        if (r1 && r2)
                            failures.Add(("d", $"the gate does not close the way: both sides of its doorway (0x{c1:X8}, 0x{c2:X8}) are reachable around it"));
                        else if (!r1 && !r2)
                            failures.Add(("d", $"neither side of the doorway (0x{c1:X8}, 0x{c2:X8}) is reachable from the anchor"));
                    }
                }
            }

            // ---- e: beams aim at the planned levers ----
            if (r.Type == PuzzleGateType.Beam && r.BeamTargets != null)
            {
                var lights = round.Where(o => o.Role == PuzzleRole.Light && o.Entered && o.Final.HasValue && o.FinalRotation.HasValue).OrderBy(o => o.Slot).ToList();

                if (lights.Count != r.BeamTargets.Count)
                    failures.Add(("e", $"{lights.Count} beam host(s) in the world for {r.BeamTargets.Count} planned beam(s)"));

                for (var j = 0; j < Math.Min(lights.Count, r.BeamTargets.Count); j++)
                {
                    var hit = BeamHit(lights[j].Final.Value, PuzzleBeamAim.BeamDirection(lights[j].FinalRotation.Value, r.Axis), levers);

                    if (hit != r.BeamTargets[j])
                        failures.Add(("e", $"beam {j + 1} points at lever {(hit < 0 ? "none" : (hit + 1).ToString(inv))}, planned lever {r.BeamTargets[j] + 1}"));
                }
            }

            return Verdict(failures, uncheckedNotes, info);
        }

        /// <summary>"doors passed: wcid 412 x3, wcid 7210 x1" (legs per door), or null when the probe opened none.</summary>
        public static string DoorsPassedNote(IEnumerable<ProbeLeg> legs)
        {
            var counts = legs?.Where(l => l.DoorsPassed != null).SelectMany(l => l.DoorsPassed).GroupBy(d => d, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} x{g.Count()}").ToList();

            return counts == null || counts.Count == 0 ? null : "doors passed: " + string.Join(", ", counts);
        }

        private static SweepOutcome Verdict(List<(string Check, string Detail)> failures, List<(string Check, string Note)> uncheckedNotes, List<string> info)
        {
            var tail = info.Count == 0 ? "" : "; " + string.Join("; ", info);

            if (failures.Count > 0)
                return new SweepOutcome(Fail, failures[0].Check, string.Join("; ", failures.Select(f => f.Check + ": " + f.Detail)) + tail);

            return uncheckedNotes.Count > 0
                ? new SweepOutcome(Unchecked, uncheckedNotes[0].Check, string.Join("; ", uncheckedNotes.Select(n => n.Note)) + tail)
                : new SweepOutcome(Pass, "-", "a-e ok (c: walk probe)" + tail);
        }

        /// <summary>
        /// The slot of the lever a beam from <paramref name="origin"/> along <paramref name="direction"/> hits: the lever
        /// whose beam target point (lever + BeamTargetHeight) lies nearest the ray, ahead of the host. -1 when none is ahead.
        /// </summary>
        public static int BeamHit(Vector3 origin, Vector3 direction, IReadOnlyList<SweepObject> levers)
        {
            if (direction.LengthSquared() < 1e-12f)
                return -1;

            direction = Vector3.Normalize(direction);
            var best = -1;
            var bestDistance = float.MaxValue;

            foreach (var l in levers)
            {
                if (!l.Final.HasValue)
                    continue;

                var target = l.Final.Value + new Vector3(0, 0, PuzzleGateTunables.BeamTargetHeight);
                var t = Vector3.Dot(target - origin, direction);

                if (t <= 0f)
                    continue;

                var miss = Vector3.Distance(origin + direction * t, target);

                if (miss < bestDistance)
                {
                    bestDistance = miss;
                    best = l.Slot;
                }
            }

            return best;
        }

        private static string Name(SweepObject o)
        {
            switch (o.Role)
            {
                case PuzzleRole.Candidate: return $"lever {o.Slot + 1}";
                case PuzzleRole.Light: return $"light {o.Slot + 1}";
                case PuzzleRole.Indicator: return $"indicator {o.Slot + 1}";
                default: return $"gate part {o.Slot + 1}";
            }
        }

        // ---- TSV ------------------------------------------------------------------------------------------

        public const string TsvHeader = "dungeon\tsite\tkind\trole\ttype\tn\tresult\tfailing_check\tprobe\telapsed_ms\tdetail";

        /// <summary>The TSV's comment line: what c does and does not check.</summary>
        public const string TsvNote = "# each row = one seed (detail starts seed=N; seeds=K sweeps K per row) at the site's largest lever count. c = cell under every lever point + walk probe: a player-sized body (Setup 0x02000001) walks the server's collision geometry, gate parts standing, from the nearest standing point within 1.5 m of the anchor (in the anchor's region cell first, else a cell the walk graph reaches from it, within step-up height + 0.1 m of its floor) to each target (each lever where it stands, further shuffle spots) until the server's use check passes with a 0.15 m margin (Position.CylinderDistance to the lever cylinder <= its UseRadius - 0.15, 0.6 m default) and the lever is in sight from eye height, and back to within 1.0 m of that start; unlocked retail doors are walked through (named in detail as doors passed), puzzle gate parts stay solid; no jumps.";

        public static string TsvRow(SweepRecord r, SweepOutcome o)
            => string.Join("\t", new[]
            {
                Clean(r.Dungeon), Clean(r.SiteId), Clean(r.SiteKind) + (string.IsNullOrEmpty(r.SiteState) ? "" : "," + r.SiteState), Clean(r.Role),
                r.Type.ToString().ToLowerInvariant(), r.N.ToString(CultureInfo.InvariantCulture), o.Result, o.FailingCheck,
                ProbeSummary(r.Probe) + (r.Probe == null ? "" : $" ({r.ProbeMs.ToString("0.##", CultureInfo.InvariantCulture)} ms)"), r.ElapsedMs.ToString(CultureInfo.InvariantCulture),
                Clean((r.Seed != 0 ? $"seed={r.Seed.ToString(CultureInfo.InvariantCulture)}; " : "") + o.Detail),
            });

        /// <summary>The probe column: "pass 12/12", "blocked 2/12", "unchecked 3/12" (blocked wins), or "-" when the probe did not run.</summary>
        public static string ProbeSummary(IReadOnlyList<ProbeLeg> probe)
        {
            if (probe == null || probe.Count == 0)
                return "-";

            var blocked = probe.Count(l => l.Outcome == ProbeOutcome.Blocked);
            var skipped = probe.Count(l => l.Outcome == ProbeOutcome.Unchecked);

            if (blocked > 0)
                return $"blocked {blocked}/{probe.Count}";

            return skipped > 0 ? $"unchecked {skipped}/{probe.Count}" : $"pass {probe.Count}/{probe.Count}";
        }

        // ---- the walk probe's pure parts ----------------------------------------------------------------------

        /// <summary>The probe body arrives when its feet are within this distance (3D) of the target.</summary>
        public const float ProbeArrival = 0.5f;

        /// <summary>The server's use range when the object sets none (WorldObject.IsWithinUseRadiusOf, Player_Move.CreateMoveToChain).</summary>
        public const float DefaultUseRadius = 0.6f;

        /// <summary>
        /// The server's use distance: Position.CylinderDistance between the player's cylinder (radius, height, at the
        /// body's cell and point) and the used object's (as WorldObject.GetCylinderDistance).
        /// </summary>
        public static float UseDistance(float bodyRadius, float bodyHeight, uint bodyCell, Vector3 body, UseReach reach, uint targetCell, Vector3 target)
        {
            var from = new ACE.Server.Physics.Common.Position(bodyCell, new ACE.Server.Physics.Animation.AFrame(body, Quaternion.Identity));
            var to = new ACE.Server.Physics.Common.Position(targetCell, new ACE.Server.Physics.Animation.AFrame(target, Quaternion.Identity));
            return (float)ACE.Server.Physics.Common.Position.CylinderDistance(bodyRadius, bodyHeight, from, reach.Radius, reach.Height, to);
        }

        /// <summary>
        /// Reached when the use distance is within the object's UseRadius less <see cref="UseMargin"/>, the object is in
        /// sight (<paramref name="visible"/>; null = not looked for, which only happens out of range), and the body
        /// stands on a walkable floor. Pure.
        /// </summary>
        public static PuzzleWalkProbe.ArrivalCheck UseArrival(float useDistance, UseReach reach, bool onWalkable, bool? visible)
        {
            var text = string.Format(CultureInfo.InvariantCulture, "use distance {0:0.00} m (server cylinder distance) vs UseRadius {1:0.00} m less {2:0.00} m margin of {3}", useDistance, reach.UseRadius, UseMargin, reach.Source);

            if (useDistance > reach.UseRadius - UseMargin)
                return new PuzzleWalkProbe.ArrivalCheck(false, text);

            if (visible != true)
                return new PuzzleWalkProbe.ArrivalCheck(false, text + ", not in sight from eye height");

            return new PuzzleWalkProbe.ArrivalCheck(onWalkable, onWalkable ? text : text + ", not on a walkable floor");
        }

        /// <summary>
        /// The return leg: within PuzzleWalkProbe.StandRadius of the start point used (3D), no more than
        /// <paramref name="stepUp"/> above or below it (<paramref name="dz"/> = end - start: a start on a ledge taller
        /// than a step is not "returned to" from the floor below it), on a walkable floor. Pure.
        /// </summary>
        public static PuzzleWalkProbe.ArrivalCheck ReturnArrival(float distance, float dz, float stepUp, bool onWalkable)
        {
            var text = string.Format(CultureInfo.InvariantCulture, "{0:0.00} m from the start point (3D), limit {1:0.0} m", distance, PuzzleWalkProbe.StandRadius);

            if (distance > PuzzleWalkProbe.StandRadius)
                return new PuzzleWalkProbe.ArrivalCheck(false, text);

            if (Math.Abs(dz) > stepUp)
                return new PuzzleWalkProbe.ArrivalCheck(false, text + string.Format(CultureInfo.InvariantCulture, ", but {0:0.00} m up/down from it, more than the {1:0.00} m step-up height", dz, stepUp));

            return new PuzzleWalkProbe.ArrivalCheck(onWalkable, onWalkable ? text : text + ", not on a walkable floor");
        }

        /// <summary>Where a player stands to face a reward scene's focal: this far short of the gate point, toward the anchor.</summary>
        public const float FocalApproachBack = 2.0f;

        /// <summary>Pass when the body ended within <see cref="ProbeArrival"/> of <paramref name="target"/> on a walkable surface.</summary>
        public static ProbeLeg ClassifyArrival(Vector3 at, bool onWalkable, Vector3 target)
        {
            var miss = Vector3.Distance(at, target);

            if (miss > ProbeArrival)
                return ProbeLeg.Blocked(null, null, at, string.Format(CultureInfo.InvariantCulture, "ended {0:0.00} m from the target ({1:0.00} m up/down)", miss, at.Z - target.Z));

            return onWalkable ? ProbeLeg.Passed(at) : ProbeLeg.Blocked(null, null, at, "reached the target but not on a walkable surface");
        }

        /// <summary>
        /// The reward scene's focal approach point: <see cref="FocalApproachBack"/> short of the gate point along the
        /// site's forward axis, at the anchor's floor height.
        /// </summary>
        public static Vector3 FocalApproach(Vector3 anchor, float yawDeg, float gateDistance)
            => PuzzleGateGenerator.ToWorld(anchor, yawDeg, new Vector3(0f, Math.Max(0f, gateDistance - FocalApproachBack), 0f));

        /// <summary>
        /// The probe's targets for a recorded placement: every lever point (planned position, resolved cell), every
        /// further shuffle spot, and a focal approach when the caller passes one (the sweep passes null: players use levers, not the focal). Points with no cell are left to c's cell check.
        /// </summary>
        public static List<ProbeTarget> ProbeTargets(IEnumerable<(int Slot, Vector3 Point, uint Cell)> levers, IEnumerable<SweepPoint> extraSpots, ProbeTarget? focalApproach)
        {
            var targets = new List<ProbeTarget>();

            foreach (var l in levers.OrderBy(l => l.Slot))
                if (l.Cell != 0)
                    targets.Add(new ProbeTarget($"lever {l.Slot + 1}", l.Cell, l.Point));

            foreach (var s in extraSpots ?? Array.Empty<SweepPoint>())
                if (s.Cell != 0)
                    targets.Add(new ProbeTarget($"shuffle spot {s.Slot + 1}", s.Cell, s.Point));

            if (focalApproach.HasValue && focalApproach.Value.Cell != 0)
                targets.Add(focalApproach.Value);

            return targets;
        }

        /// <summary>Tabs, newlines and non-ASCII out of a TSV cell.</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            var sb = new StringBuilder(s.Length);

            foreach (var ch in s)
                sb.Append(ch == '\t' || ch == '\r' || ch == '\n' ? ' ' : ch < 32 || ch > 126 ? '?' : ch);

            return sb.ToString();
        }

        /// <summary>Per-dungeon counts: rows, pass, fail, unchecked, and fails per check. One line per dungeon, then a total.</summary>
        public static List<string> Summary(IEnumerable<(SweepRecord Record, SweepOutcome Outcome)> rows)
        {
            var lines = new List<string>();
            var all = rows.ToList();

            string Line(string label, List<(SweepRecord Record, SweepOutcome Outcome)> g)
            {
                var byCheck = g.Where(x => x.Outcome.Result == Fail).GroupBy(x => x.Outcome.FailingCheck).OrderBy(x => x.Key)
                    .Select(x => $"{x.Key}={x.Count()}");
                var gateFails = g.Count(x => x.Outcome.Result == Fail && x.Record.Role == "gate");

                var ms = g.Sum(x => x.Record.ElapsedMs);
                var probeMs = g.Sum(x => x.Record.ProbeMs);
                var startMs = g.Sum(x => x.Record.StartMs);
                var worst = g.Count == 0 ? 0 : g.Max(x => x.Record.MaxActionMs);

                return $"{label}: {g.Count} rows, {g.Count(x => x.Outcome.Result == Pass)} pass, {g.Count(x => x.Outcome.Result == Fail)} fail ({gateFails} gate-role), {g.Count(x => x.Outcome.Result == Unchecked)} unchecked; fails by check: {(byCheck.Any() ? string.Join(" ", byCheck) : "none")}; elapsed {ms} ms ({(g.Count == 0 ? 0 : ms / g.Count)} ms/row, probe {probeMs.ToString("0.#", CultureInfo.InvariantCulture)} ms on the landblock thread, start search {startMs.ToString("0.#", CultureInfo.InvariantCulture)} ms, worst action {worst.ToString("0.##", CultureInfo.InvariantCulture)} ms)";
            }

            foreach (var g in all.GroupBy(x => x.Record.Dungeon).OrderBy(x => x.Key, StringComparer.Ordinal))
                lines.Add(Line(g.Key, g.ToList()));

            lines.Add(Line("TOTAL", all));
            return lines;
        }

        // ---- the kill-switch decision ----------------------------------------------------------------------

        /// <summary>What --write-disabled sets on one site.</summary>
        public sealed record DisableDecision(string Dungeon, string SiteId, bool RewardOnly, string Reason);

        /// <summary>
        /// The sites to switch off. A FAILED gate role disables the whole site ("disabled": a broken gate can block
        /// progress). A failed reward role disables a reward site whole (that is its only use) and a gate site for the
        /// reward role only ("rewardDisabled"), since a reward host that fails never blocks a doorway. Unchecked rows
        /// change nothing. A site already switched off for that role is skipped.
        /// </summary>
        public static List<DisableDecision> Decide(IEnumerable<(SweepRecord Record, SweepOutcome Outcome)> rows, string stamp)
        {
            var decisions = new List<DisableDecision>();

            foreach (var g in rows.Where(x => x.Outcome.Result == Fail).GroupBy(x => (x.Record.Dungeon, x.Record.SiteId)))
            {
                var first = g.First().Record;
                var gateFail = g.FirstOrDefault(x => x.Record.Role == "gate");
                var rewardFail = g.FirstOrDefault(x => x.Record.Role == "reward");

                if (gateFail.Record != null)
                {
                    if (first.SiteState != "disabled")
                        decisions.Add(new DisableDecision(first.Dungeon, first.SiteId, false, Reason(stamp, gateFail)));
                }
                else if (rewardFail.Record != null)
                {
                    var rewardOnly = first.SiteKind == "gate";

                    if (first.SiteState != "disabled" && !(rewardOnly && first.SiteState == "reward-disabled"))
                        decisions.Add(new DisableDecision(first.Dungeon, first.SiteId, rewardOnly, Reason(stamp, rewardFail)));
                }
            }

            return decisions;
        }

        /// <summary>The longest outcome detail a disabledReason carries; longer text is cut and ends "...".</summary>
        public const int MaxReasonDetail = 300;

        internal static string Reason(string stamp, (SweepRecord Record, SweepOutcome Outcome) row)
        {
            var detail = row.Outcome.Detail ?? "";

            if (detail.Length > MaxReasonDetail)
                detail = detail.Substring(0, MaxReasonDetail - 3) + "...";

            return $"puzzlegate-sweep {stamp}: {row.Record.Role} {row.Record.Type.ToString().ToLowerInvariant()} check {row.Outcome.FailingCheck}: {detail}";
        }
    }

    /// <summary>
    /// Writes the kill switch into puzzle-gates.json TEXT without re-serialising it: new property lines are inserted just
    /// above the site's "id" line, at its indentation and with the file's own line ending, and every other byte of the
    /// file is left exactly as read. Pure.
    /// </summary>
    public static class PuzzleSiteKillSwitch
    {
        /// <summary>
        /// The file text with <paramref name="siteId"/> of <paramref name="dungeonId"/> switched off ("disabled", or
        /// "rewardDisabled" when <paramref name="rewardOnly"/>) with a "disabledReason". Unchanged text (and false) when
        /// the site already carries that switch; throws FormatException when the dungeon or site is not found.
        /// </summary>
        public static bool TryApply(string text, string dungeonId, string siteId, bool rewardOnly, string reason, out string result)
        {
            result = text;

            var dungeon = Regex.Match(text, "^([ \\t]*)\"" + Regex.Escape(dungeonId) + "\"[ \\t]*:[ \\t]*\\{", RegexOptions.Multiline);

            if (!dungeon.Success)
                throw new FormatException($"dungeon '{dungeonId}' not found");

            // The dungeon's extent: up to the next key at the dungeon's own indentation (or the end of the file).
            var indent = Regex.Escape(dungeon.Groups[1].Value);
            var next = new Regex("^" + indent + "\"[^\"]+\"[ \\t]*:[ \\t]*\\{", RegexOptions.Multiline).Match(text, dungeon.Index + dungeon.Length);
            var end = next.Success ? next.Index : text.Length;

            var idLine = new Regex("^([ \\t]*)\"id\"[ \\t]*:[ \\t]*\"" + Regex.Escape(siteId) + "\"[ \\t]*,?[ \\t]*(\\r?\\n)", RegexOptions.Multiline);
            var site = idLine.Match(text, dungeon.Index, end - dungeon.Index);

            if (!site.Success)
                throw new FormatException($"site '{siteId}' not found in dungeon '{dungeonId}'");

            var siteIndent = site.Groups[1].Value;
            var newline = site.Groups[2].Value;

            // The site's own extent: its opening brace (the last '{' before the id line) to the next "id" line at the same
            // indentation, or the dungeon's end. Only that range is searched for an existing switch.
            var siteStart = text.LastIndexOf('{', site.Index);
            var nextId = new Regex("^" + Regex.Escape(siteIndent) + "\"id\"[ \\t]*:", RegexOptions.Multiline).Match(text, site.Index + site.Length, end - (site.Index + site.Length));
            var siteEnd = nextId.Success ? nextId.Index : end;
            var body = text.Substring(siteStart, siteEnd - siteStart);

            var key = rewardOnly ? "rewardDisabled" : "disabled";

            // An existing key of ANY value is rewritten in place (a "disabled": false becomes true), never duplicated.
            var keyMatch = new Regex("\"" + key + "\"[ \\t]*:[ \\t]*(true|false|null|-?[0-9.eE+-]+|\"(?:[^\"\\\\]|\\\\.)*\")").Match(text, siteStart, siteEnd - siteStart);

            if (keyMatch.Success && keyMatch.Groups[1].Value == "true")
                return false;

            var reasonMatch = new Regex("\"disabledReason\"[ \\t]*:[ \\t]*(null|\"(?:[^\"\\\\]|\\\\.)*\")").Match(text, siteStart, siteEnd - siteStart);
            var reasonLiteral = JsonString(reason);

            var edits = new List<(int Index, int Length, string Text)>();
            var insert = new StringBuilder();

            if (keyMatch.Success)
                edits.Add((keyMatch.Groups[1].Index, keyMatch.Groups[1].Length, "true"));
            else
                insert.Append(siteIndent).Append('"').Append(key).Append("\": true,").Append(newline);

            if (reasonMatch.Success)
                edits.Add((reasonMatch.Groups[1].Index, reasonMatch.Groups[1].Length, reasonLiteral));
            else
                insert.Append(siteIndent).Append("\"disabledReason\": ").Append(reasonLiteral).Append(',').Append(newline);

            if (insert.Length > 0)
                edits.Add((site.Index, 0, insert.ToString()));

            var sb = new StringBuilder(text);

            foreach (var e in edits.OrderByDescending(e => e.Index))
                sb.Remove(e.Index, e.Length).Insert(e.Index, e.Text);

            result = sb.ToString();
            return true;
        }

        /// <summary>A JSON string literal of ASCII text (quotes and backslashes escaped, anything else outside printable ASCII replaced).</summary>
        public static string JsonString(string s)
        {
            var sb = new StringBuilder("\"");

            foreach (var ch in s ?? "")
            {
                if (ch == '"' || ch == '\\')
                    sb.Append('\\').Append(ch);
                else if (ch < 32 || ch > 126)
                    sb.Append('?');
                else
                    sb.Append(ch);
            }

            return sb.Append('"').ToString();
        }
    }
}
