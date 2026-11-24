using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

using ACE.Server.Entity;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// One curated spawn point offered to the encounter picker, with the distances its requirements read. Built by
    /// ThreadEncounterPass.BuildCandidates (or directly by a test). Immutable.
    /// </summary>
    public sealed class ThreadEncounterCandidate
    {
        public ThreadEncounterCandidate(DungeonSpawnPointDef point, Vector3 global, float entryDistance, float bossDistance)
        {
            Point = point ?? throw new ArgumentNullException(nameof(point));
            Global = global;
            EntryDistance = entryDistance;
            BossDistance = bossDistance;
        }

        public DungeonSpawnPointDef Point { get; }

        /// <summary>
        /// The point in the run's comparison frame (<see cref="ThreadEncounterGeometry.Global"/>): landblock-local for a
        /// dungeon (one landblock), global outdoor metres for an outdoor run.
        /// </summary>
        public Vector3 Global { get; }

        /// <summary>Metres from the run's entry: walked when the dungeon has a walk graph, else straight-line.</summary>
        public float EntryDistance { get; }

        /// <summary>Metres from the boss anchor: walked when the dungeon has a walk graph, else straight-line. MaxValue when the run has no boss anchor.</summary>
        public float BossDistance { get; }

        /// <summary>The landblock the point is on (the one whose action queue places it).</summary>
        public ushort Landblock => (ushort)(Point.Cell >> 16);

        public override string ToString()
            => string.Format(CultureInfo.InvariantCulture, "0x{0:X8} ({1:0.##}, {2:0.##}, {3:0.##})", Point.Cell, Point.X, Point.Y, Point.Z);
    }

    /// <summary>Pure geometry shared by the encounter picker and pass.</summary>
    public static class ThreadEncounterGeometry
    {
        /// <summary>
        /// A point in the run's comparison frame. Dungeon: landblock-local x, y, z (every point of a dungeon is on its one
        /// landblock). Outdoor: global outdoor metres (OutdoorSiteGeometry), so points on different footprint landblocks
        /// compare directly.
        /// </summary>
        public static Vector3 Global(uint cell, float x, float y, float z, bool isOutdoor)
        {
            if (!isOutdoor)
                return new Vector3(x, y, z);

            var lb = (ushort)(cell >> 16);
            return new Vector3((float)OutdoorSiteGeometry.ToGlobalX(lb, x), (float)OutdoorSiteGeometry.ToGlobalY(lb, y), z);
        }

        public static Vector3 Global(DungeonSpawnPointDef p, bool isOutdoor) => Global(p.Cell, p.X, p.Y, p.Z, isOutdoor);

        /// <summary>Straight-line distance: 3D inside a dungeon (floors stack), horizontal outdoors (the outdoor rings are XY, OutdoorSiteGeometry).</summary>
        public static float Distance(Vector3 a, Vector3 b, bool isOutdoor)
            => isOutdoor ? Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)) : Vector3.Distance(a, b);
    }

    /// <summary>One encounter the run will place: the slot it filled, the type row, the point and the placement's own seed.</summary>
    public readonly record struct ThreadEncounterPick(int Slot, ThreadEncounterTypeDef Def, ThreadEncounterCandidate Point, int Seed);

    /// <summary>The picker's output: the picks, and one line per slot that rolled but could not be filled.</summary>
    public sealed class ThreadEncounterPlan
    {
        public const string MechanismCountWeights = "countWeights";
        public const string MechanismSlotChance = "slot_chance";

        public ThreadEncounterPlan(IReadOnlyList<ThreadEncounterPick> picks, IReadOnlyList<string> dropped, int slotsRolled, int slotsActive, string skipped, string mechanism = null,
            IReadOnlyDictionary<int, IReadOnlyList<ThreadEncounterPick>> fallbacks = null)
        {
            Picks = picks ?? Array.Empty<ThreadEncounterPick>();
            Dropped = dropped ?? Array.Empty<string>();
            SlotsRolled = slotsRolled;
            SlotsActive = slotsActive;
            Skipped = skipped;
            Mechanism = mechanism;
            Fallbacks = fallbacks ?? new Dictionary<int, IReadOnlyList<ThreadEncounterPick>>();
        }

        public static ThreadEncounterPlan Nothing(string why) => new ThreadEncounterPlan(null, null, 0, 0, why);

        public IReadOnlyList<ThreadEncounterPick> Picks { get; }

        /// <summary>
        /// Per picked slot, the ordered alternates the pass tries when that slot's pick cannot be placed
        /// (ThreadEncounterPass.Dispatch): other points of the same row first, then other kinds. Every alternate has the
        /// pick's Slot. Empty for a slot with none. See <see cref="ThreadEncounterPicker.PlanFallbacks"/>.
        /// </summary>
        public IReadOnlyDictionary<int, IReadOnlyList<ThreadEncounterPick>> Fallbacks { get; }

        /// <summary>"slot N: why" for every active slot with no fit.</summary>
        public IReadOnlyList<string> Dropped { get; }

        /// <summary>How many slots were rolled (the clamped max_per_run).</summary>
        public int SlotsRolled { get; }

        /// <summary>How many slots the run tried to fill: the drawn count (countWeights) or the slots that won their slot_chance roll.</summary>
        public int SlotsActive { get; }

        /// <summary>Which mechanism chose the count: <see cref="MechanismCountWeights"/> or <see cref="MechanismSlotChance"/>; null when skipped.</summary>
        public string Mechanism { get; }

        /// <summary>"countWeights drew 2 of max 5" / "slot_chance 0.5 x 5 slots, 2 active", for /dd and the pass log.</summary>
        public string CountSummary(double slotChance)
            => Mechanism == MechanismCountWeights
                ? $"countWeights drew {SlotsActive} of max {SlotsRolled}"
                : Mechanism == MechanismSlotChance
                    ? string.Format(CultureInfo.InvariantCulture, "slot_chance {0:0.###} x {1} slot(s), {2} active", slotChance, SlotsRolled, SlotsActive)
                    : $"{SlotsActive}/{SlotsRolled} slot(s)";

        /// <summary>Why nothing was even rolled (switch off, zero max, no types, disabled dungeon), or null.</summary>
        public string Skipped { get; }
    }

    /// <summary>Everything one pick needs, so <see cref="ThreadEncounterPicker.Pick"/> stays pure.</summary>
    public sealed class ThreadEncounterPickRequest
    {
        public IReadOnlyList<ThreadEncounterTypeDef> Types { get; set; } = Array.Empty<ThreadEncounterTypeDef>();

        /// <summary>Is an implementation registered for this kind? Null = every kind is.</summary>
        public Func<string, bool> KindRegistered { get; set; }

        /// <summary>The implementation's own extra point filter (IThreadEncounterType.RejectPoint): null accepts, a string rejects. Optional.</summary>
        public Func<ThreadEncounterTypeDef, ThreadEncounterCandidate, string> TypeFilter { get; set; }

        /// <summary>The implementation's per-run filter (IThreadEncounterType.RejectRun): null allows the type, a string drops it from the draw with that reason. Optional.</summary>
        public Func<ThreadEncounterTypeDef, string> RunFilter { get; set; }

        public IReadOnlyList<ThreadEncounterCandidate> Candidates { get; set; } = Array.Empty<ThreadEncounterCandidate>();

        public bool IsOutdoor { get; set; }

        /// <summary>This run's puzzle sites, in the same frame as <see cref="ThreadEncounterCandidate.Global"/>.</summary>
        public IReadOnlyList<Vector3> PuzzleSites { get; set; } = Array.Empty<Vector3>();

        public bool Enabled { get; set; }

        public int MaxPerRun { get; set; }

        public double SlotChance { get; set; }

        /// <summary>
        /// encounters.json "countWeights" (ThreadEncounterStore.CountWeights): when non-null the run draws its encounter
        /// count N from these weights and fills the first N slots; SlotChance is then unused. Null = the slot_chance roll.
        /// </summary>
        public IReadOnlyList<double> CountWeights { get; set; }

        public int RunSeed { get; set; }

        /// <summary>
        /// Outdoor only: does the ground at the point, and at 4 points on a ring of the given radius around it, take an
        /// encounter? Null accepts. Never called for a dungeon run, whose points carry a curated clearance instead.
        /// </summary>
        public Func<ThreadEncounterCandidate, float, bool> GroundProbe { get; set; }
    }

    /// <summary>
    /// Chooses a run's encounters. PURE and deterministic given its request (the ground probe is the one seam, and
    /// production's is a read of static terrain): the same request always gives the same plan, so a run is
    /// reproducible from its gem.
    ///
    /// Its OWN stream: a xorshift (SkyDecorRandom) seeded from the run seed with its own salt, never the population
    /// builder's System.Random and never the puzzle picker's stream, so adding encounters cannot move a single
    /// creature or puzzle draw (DungeonPopulationBuilder's plan notes feed a pinned fingerprint).
    ///
    /// Rules: the count comes first, so it never depends on what fits. With CountWeights, ONE draw picks N by weight
    /// (index = count; weights for counts above MaxPerRun fold into MaxPerRun, so lowering the cap never changes the
    /// chance of zero) and the first N slots are active. Without, each of MaxPerRun slots rolls SlotChance
    /// independently (all rolls drawn first). Each active slot, in order, draws a type by weight among the enabled, registered,
    /// run-kind-allowed types that still have at least one valid point (requirements AND spacing from the encounters
    /// already picked and the run's puzzle sites); then one of that type's valid points uniformly; then a placement
    /// seed. A slot with no fitting type is dropped with its reason.
    /// </summary>
    public static class ThreadEncounterPicker
    {
        /// <summary>The hard ceiling on slots, whatever the tunable says.</summary>
        public const int MaxPerRunCeiling = 8;

        /// <summary>The stream salt. Distinct from ThreadPuzzleSitePicker's 0x5A17E5ED.</summary>
        public const uint StreamSalt = 0xE7C0D7E5u;

        public static ThreadEncounterPlan Pick(ThreadEncounterPickRequest request)
        {
            if (request == null)
                return ThreadEncounterPlan.Nothing("no request");

            if (!request.Enabled)
                return ThreadEncounterPlan.Nothing("encounters are switched off for this run");

            var maxPerRun = Math.Clamp(request.MaxPerRun, 0, MaxPerRunCeiling);

            if (maxPerRun == 0)
                return ThreadEncounterPlan.Nothing("max_per_run is 0");

            var slotChance = double.IsNaN(request.SlotChance) ? 0.0 : Math.Clamp(request.SlotChance, 0.0, 1.0);
            var types = request.Types ?? Array.Empty<ThreadEncounterTypeDef>();
            var candidates = request.Candidates ?? Array.Empty<ThreadEncounterCandidate>();
            var puzzles = request.PuzzleSites ?? Array.Empty<Vector3>();

            var rng = NewStream(request.RunSeed);

            // The count first: a function of the seed and the weights (or chance) alone.
            var active = new bool[maxPerRun];
            string mechanism;

            if (request.CountWeights != null && request.CountWeights.Count > 0)
            {
                mechanism = ThreadEncounterPlan.MechanismCountWeights;
                var count = DrawCount(FoldCountWeights(request.CountWeights, maxPerRun), rng.NextDouble());

                for (var i = 0; i < count; i++)
                    active[i] = true;
            }
            else
            {
                mechanism = ThreadEncounterPlan.MechanismSlotChance;

                for (var i = 0; i < maxPerRun; i++)
                    active[i] = rng.NextDouble() < slotChance;
            }

            var picks = new List<ThreadEncounterPick>();
            var dropped = new List<string>();

            // Requirement verdicts are per (type row, point) and do not change between slots; spacing does.
            var probeCache = new Dictionary<(ThreadEncounterCandidate, float), bool>();
            var fitCache = new Dictionary<ThreadEncounterTypeDef, (List<ThreadEncounterCandidate> Fits, string Why)>();

            for (var slot = 0; slot < maxPerRun; slot++)
            {
                if (!active[slot])
                    continue;

                var fitting = new List<(ThreadEncounterTypeDef Def, List<ThreadEncounterCandidate> Points)>();
                var whys = new List<string>();

                foreach (var def in types)
                {
                    if (def == null)
                        continue;

                    var typeWhy = KindCapReached(def, picks) ?? TypeUnavailable(def, request);

                    if (typeWhy != null)
                    {
                        whys.Add($"{def.Id}: {typeWhy}");
                        continue;
                    }

                    if (!fitCache.TryGetValue(def, out var fit))
                    {
                        fit = PointsMeetingRequirements(def, candidates, request, probeCache);
                        fitCache[def] = fit;
                    }

                    if (fit.Fits.Count == 0)
                    {
                        whys.Add($"{def.Id}: {fit.Why}");
                        continue;
                    }

                    var spaced = fit.Fits.Where(c => Spaced(c, def.Placement?.MinSpacing ?? 0f, picks, puzzles, request.IsOutdoor)).ToList();

                    if (spaced.Count == 0)
                    {
                        whys.Add($"{def.Id}: no valid point is clear of the other encounters and puzzle sites");
                        continue;
                    }

                    fitting.Add((def, spaced));
                }

                if (fitting.Count == 0)
                {
                    dropped.Add($"slot {slot}: no type fits" + (whys.Count == 0 ? " (no types)" : " (" + string.Join("; ", whys) + ")"));
                    continue;
                }

                // Three draws per filled slot, always, whatever wins.
                var typeRoll = rng.NextDouble();
                var pointRoll = rng.NextUInt();
                var seed = (int)(rng.NextUInt() & 0x7FFFFFFFu);

                if (seed == 0)
                    seed = 1;

                var chosen = ChooseByWeight(fitting, typeRoll);
                var point = chosen.Points[(int)(pointRoll % (uint)chosen.Points.Count)];

                picks.Add(new ThreadEncounterPick(slot, chosen.Def, point, seed));
            }

            // After the slot loop, from per-pick streams: the run stream's draws (and so every primary pick) are unchanged.
            // Fail-open: alternates are a bonus. A throwing filter or probe here leaves the primary picks exactly as drawn,
            // with no alternates (each slot then drops as it did before fallbacks existed).
            Dictionary<int, IReadOnlyList<ThreadEncounterPick>> fallbacks;

            try
            {
                fallbacks = PlanFallbacks(picks, types, candidates, request, puzzles, fitCache, probeCache);
            }
            catch (Exception)
            {
                fallbacks = null;
            }

            return new ThreadEncounterPlan(picks, dropped, maxPerRun, active.Count(a => a), null, mechanism, fallbacks);
        }

        /// <summary>How many other points of the pick's own row a slot may fall back to.</summary>
        public const int MaxSameRowFallbacks = 2;

        /// <summary>How many other kinds a slot may fall back to (one point each), after its own row's points.</summary>
        public const int MaxOtherKindFallbacks = 2;

        /// <summary>The fallback stream salt, mixed with the pick's own seed.</summary>
        public const uint FallbackSalt = 0x0FA11BA6u;

        /// <summary>
        /// The alternates for each pick (<see cref="ThreadEncounterPlan.Fallbacks"/>), deterministic from the picks' seeds.
        /// Per pick, in slot order, from its own stream (<see cref="NewStream"/> of seed ^ <see cref="FallbackSalt"/>):
        /// up to <see cref="MaxSameRowFallbacks"/> other valid points of its row drawn uniformly without replacement, then up
        /// to <see cref="MaxOtherKindFallbacks"/> rows of OTHER kinds drawn by weight (one valid point each, uniformly).
        ///
        /// Whatever combination of picks and alternates ends up placed is legal, because each pair of slots is checked once:
        /// an alternate of slot s must meet the requirements, be <see cref="Spaced"/> from the pick of every other slot and
        /// from every alternate of every EARLIER slot, and keep its kind under its row's cap counting one per other slot that
        /// could place that kind (an earlier slot's pick or any of its alternates; a later slot's pick). A later slot's
        /// alternates are checked against this slot's in the same way. Pure but for the request's probe seam.
        /// </summary>
        internal static Dictionary<int, IReadOnlyList<ThreadEncounterPick>> PlanFallbacks(IReadOnlyList<ThreadEncounterPick> picks, IReadOnlyList<ThreadEncounterTypeDef> types,
            IReadOnlyList<ThreadEncounterCandidate> candidates, ThreadEncounterPickRequest request, IReadOnlyList<Vector3> puzzles,
            Dictionary<ThreadEncounterTypeDef, (List<ThreadEncounterCandidate> Fits, string Why)> fitCache, Dictionary<(ThreadEncounterCandidate, float), bool> probeCache)
        {
            var result = new Dictionary<int, IReadOnlyList<ThreadEncounterPick>>();

            if (picks == null || picks.Count == 0)
                return result;

            (List<ThreadEncounterCandidate> Fits, string Why) FitOf(ThreadEncounterTypeDef def)
            {
                if (!fitCache.TryGetValue(def, out var fit))
                {
                    fit = PointsMeetingRequirements(def, candidates, request, probeCache);
                    fitCache[def] = fit;
                }

                return fit;
            }

            // What each slot could still place, by index into picks: its pick, plus (once planned) its alternates.
            var possible = picks.Select(p => new List<ThreadEncounterPick> { p }).ToList();

            for (var i = 0; i < picks.Count; i++)
            {
                var pick = picks[i];
                var alternates = new List<ThreadEncounterPick>();

                // Every other slot's pick, and every alternate of the slots before this one.
                var against = new List<ThreadEncounterPick>();

                for (var j = 0; j < picks.Count; j++)
                {
                    if (j == i)
                        continue;

                    against.AddRange(j < i ? possible[j] : new List<ThreadEncounterPick> { picks[j] });
                }

                bool KindRoom(ThreadEncounterTypeDef def)
                {
                    if (!(def?.MaxPerRun is int cap))
                        return true;

                    var slots = 0;

                    for (var j = 0; j < picks.Count; j++)
                    {
                        if (j == i)
                            continue;

                        var could = j < i ? possible[j] : new List<ThreadEncounterPick> { picks[j] };

                        if (could.Any(p => string.Equals(p.Def?.Kind, def.Kind, StringComparison.Ordinal)))
                            slots++;
                    }

                    return slots < cap;
                }

                List<ThreadEncounterCandidate> Valid(ThreadEncounterTypeDef def)
                    => FitOf(def).Fits.Where(c => !ReferenceEquals(c, pick.Point) && Spaced(c, def.Placement?.MinSpacing ?? 0f, against, puzzles, request.IsOutdoor)).ToList();

                var rng = NewStream(unchecked(pick.Seed ^ (int)FallbackSalt));

                int NextSeed()
                {
                    var s = (int)(rng.NextUInt() & 0x7FFFFFFFu);
                    return s == 0 ? 1 : s;
                }

                // Same row, other points.
                if (pick.Def != null)
                {
                    var same = Valid(pick.Def);

                    for (var k = 0; k < MaxSameRowFallbacks && same.Count > 0; k++)
                    {
                        var at = (int)(rng.NextUInt() % (uint)same.Count);
                        alternates.Add(new ThreadEncounterPick(pick.Slot, pick.Def, same[at], NextSeed()));
                        same.RemoveAt(at);
                    }
                }

                // Other kinds, by weight, one point each.
                var others = new List<(ThreadEncounterTypeDef Def, List<ThreadEncounterCandidate> Points)>();

                foreach (var def in types ?? Array.Empty<ThreadEncounterTypeDef>())
                {
                    if (def == null || pick.Def == null || string.Equals(def.Kind, pick.Def.Kind, StringComparison.Ordinal))
                        continue;

                    if (TypeUnavailable(def, request) != null || !KindRoom(def))
                        continue;

                    var points = Valid(def);

                    if (points.Count > 0)
                        others.Add((def, points));
                }

                for (var k = 0; k < MaxOtherKindFallbacks && others.Count > 0; k++)
                {
                    var chosen = ChooseByWeight(others, rng.NextDouble());
                    var point = chosen.Points[(int)(rng.NextUInt() % (uint)chosen.Points.Count)];
                    alternates.Add(new ThreadEncounterPick(pick.Slot, chosen.Def, point, NextSeed()));

                    // One row per kind: a second row of the same kind would most likely refuse for the same reason.
                    others.RemoveAll(o => string.Equals(o.Def.Kind, chosen.Def.Kind, StringComparison.Ordinal));
                }

                possible[i].AddRange(alternates);

                if (alternates.Count > 0)
                    result[pick.Slot] = alternates;
            }

            return result;
        }

        /// <summary>
        /// The count weights as cap + 1 entries: index i &lt;= cap keeps its weight, every weight for a count above the cap is
        /// added to the cap's entry, and a count the table does not list weighs 0. Negative or non-finite entries weigh 0
        /// (the store already refuses them). Pure.
        /// </summary>
        public static double[] FoldCountWeights(IReadOnlyList<double> weights, int cap)
        {
            cap = Math.Max(0, cap);
            var folded = new double[cap + 1];

            if (weights == null)
                return folded;

            for (var i = 0; i < weights.Count; i++)
            {
                var w = weights[i];

                if (double.IsNaN(w) || double.IsInfinity(w) || w < 0)
                    continue;

                folded[Math.Min(i, cap)] += w;
            }

            return folded;
        }

        /// <summary>
        /// The count for a uniform <paramref name="roll"/> in [0, 1) over <paramref name="folded"/>: the index whose
        /// cumulative band holds roll x total. All-zero weights give 0. Pure.
        /// </summary>
        public static int DrawCount(double[] folded, double roll)
        {
            if (folded == null || folded.Length == 0)
                return 0;

            var total = folded.Sum();

            if (!(total > 0))
                return 0;

            var target = roll * total;
            var acc = 0.0;
            var last = 0;

            for (var i = 0; i < folded.Length; i++)
            {
                if (!(folded[i] > 0))
                    continue;

                last = i;
                acc += folded[i];

                if (target < acc)
                    return i;
            }

            return last;
        }

        internal static SkyDecorRandom NewStream(int runSeed)
        {
            SkyDecorRandom rng;

            unchecked
            {
                rng = new SkyDecorRandom((uint)runSeed * 2654435761u ^ StreamSalt);
            }

            for (var i = 0; i < 4; i++)
                rng.NextUInt();

            return rng;
        }

        /// <summary>
        /// The per-kind cap: the row's maxPerRun when it has one, counted over the picks already made of the same KIND
        /// (any row). A reason once the cap is reached; null with no cap or below it. Pure.
        /// </summary>
        internal static string KindCapReached(ThreadEncounterTypeDef def, IReadOnlyList<ThreadEncounterPick> picks)
        {
            if (!(def?.MaxPerRun is int cap) || picks == null)
                return null;

            var taken = picks.Count(p => string.Equals(p.Def?.Kind, def.Kind, StringComparison.Ordinal));
            return taken >= cap ? $"maxPerRun {cap} of kind '{def.Kind}' already placed" : null;
        }

        /// <summary>Why a type row cannot fill any slot of this run regardless of points, or null.</summary>
        internal static string TypeUnavailable(ThreadEncounterTypeDef def, ThreadEncounterPickRequest request)
        {
            if (!def.Enabled)
                return "disabled";

            if (!(def.Weight > 0))
                return "weight 0 (admin place only)";

            if (request.KindRegistered != null && !request.KindRegistered(def.Kind))
                return $"kind '{def.Kind}' is not registered";

            if (!(def.Placement ?? new ThreadEncounterPlacementDef()).AllowsRunKind(request.IsOutdoor))
                return $"not allowed in {(request.IsOutdoor ? "outdoor" : "dungeon")} runs";

            var runWhy = request.RunFilter?.Invoke(def);

            if (runWhy != null)
                return runWhy;

            return null;
        }

        private static (List<ThreadEncounterCandidate> Fits, string Why) PointsMeetingRequirements(ThreadEncounterTypeDef def,
            IReadOnlyList<ThreadEncounterCandidate> candidates, ThreadEncounterPickRequest request, Dictionary<(ThreadEncounterCandidate, float), bool> probeCache)
        {
            var fits = new List<ThreadEncounterCandidate>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var c in candidates)
            {
                if (c == null)
                    continue;

                var why = RejectReason(def, c, request.IsOutdoor, ProbeOf(request, probeCache), request.TypeFilter);

                if (why == null)
                    fits.Add(c);
                else
                    counts[why] = counts.TryGetValue(why, out var n) ? n + 1 : 1;
            }

            if (fits.Count > 0)
                return (fits, null);

            if (candidates.Count == 0)
                return (fits, "the run has no candidate points");

            return (fits, "no point meets its requirements (" + string.Join(", ", counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} x{kv.Value}")) + ")");
        }

        private static Func<ThreadEncounterCandidate, float, bool> ProbeOf(ThreadEncounterPickRequest request, Dictionary<(ThreadEncounterCandidate, float), bool> cache)
        {
            if (request.GroundProbe == null)
                return null;

            return (c, r) =>
            {
                if (!cache.TryGetValue((c, r), out var ok))
                {
                    ok = request.GroundProbe(c, r);
                    cache[(c, r)] = ok;
                }

                return ok;
            };
        }

        /// <summary>
        /// Why <paramref name="c"/> cannot hold an encounter of this type, or null. The requirement half only (no
        /// spacing: that depends on the other picks). Ordered cheapest first; the ground probe is last because it reads
        /// terrain. Pure but for <paramref name="groundProbe"/>.
        /// </summary>
        public static string RejectReason(ThreadEncounterTypeDef def, ThreadEncounterCandidate c, bool isOutdoor,
            Func<ThreadEncounterCandidate, float, bool> groundProbe, Func<ThreadEncounterTypeDef, ThreadEncounterCandidate, string> typeFilter = null)
        {
            var p = def?.Placement ?? new ThreadEncounterPlacementDef();
            var point = c.Point;

            if (!point.Curated)
                return "not curated";

            if (isOutdoor)
            {
                // Open ground only: an interior point sits under a building's roof, which no encounter is shaped for.
                if (!string.Equals(point.Kind, "outdoor", StringComparison.Ordinal))
                    return "not an outdoor point";
            }
            else
            {
                if (point.Clearance < p.MinClearance)
                    return "clearance";

                if (p.MinDepth.HasValue && point.Depth < p.MinDepth.Value)
                    return "too shallow";

                if (p.MaxDepth.HasValue && point.Depth > p.MaxDepth.Value)
                    return "too deep";
            }

            if (c.EntryDistance < p.MinEntryWalk)
                return "near entry";

            if (c.BossDistance < p.MinBossDist)
                return "near boss";

            if (typeFilter != null)
            {
                var own = typeFilter(def, c);

                if (own != null)
                    return own;
            }

            if (isOutdoor && groundProbe != null && !groundProbe(c, p.MinClearance))
                return "ground probe";

            return null;
        }

        /// <summary>
        /// Is <paramref name="c"/> clear of every picked encounter and every puzzle site? Spacing between two encounters is
        /// SYMMETRIC: the larger of the candidate's <paramref name="minSpacing"/> and the other pick's own minSpacing, so a
        /// spacing-0 type can never land inside a spaced type's radius. Puzzle sites use the candidate's spacing. Two picks
        /// never share a point.
        /// </summary>
        public static bool Spaced(ThreadEncounterCandidate c, float minSpacing, IReadOnlyList<ThreadEncounterPick> picked, IReadOnlyList<Vector3> puzzleSites, bool isOutdoor)
        {
            if (picked != null)
            {
                foreach (var other in picked)
                {
                    if (ReferenceEquals(other.Point, c))
                        return false;

                    var need = Math.Max(minSpacing > 0 ? minSpacing : 0f, other.Def?.Placement?.MinSpacing ?? 0f);

                    if (need > 0 && ThreadEncounterGeometry.Distance(other.Point.Global, c.Global, isOutdoor) < need)
                        return false;
                }
            }

            if (!(minSpacing > 0))
                return true;

            if (puzzleSites != null)
            {
                foreach (var site in puzzleSites)
                {
                    if (ThreadEncounterGeometry.Distance(site, c.Global, isOutdoor) < minSpacing)
                        return false;
                }
            }

            return true;
        }

        private static (ThreadEncounterTypeDef Def, List<ThreadEncounterCandidate> Points) ChooseByWeight(
            List<(ThreadEncounterTypeDef Def, List<ThreadEncounterCandidate> Points)> fitting, double roll)
        {
            var total = fitting.Sum(f => f.Def.Weight);
            var target = roll * total;
            var running = 0.0;

            foreach (var f in fitting)
            {
                running += f.Def.Weight;

                if (target < running)
                    return f;
            }

            return fitting[fitting.Count - 1];
        }
    }
}
