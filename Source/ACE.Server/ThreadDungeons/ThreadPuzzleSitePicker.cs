using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.Entity;
using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>One puzzle the run will place: which site, which type, how many levers, and the placement's own seed.</summary>
    public readonly record struct ThreadPuzzlePick(PuzzleSiteDef Site, PuzzleGateType Type, int N, int Seed, bool IsReward);

    /// <summary>
    /// Chooses a run's puzzles from its dungeon's curated sites. PURE and deterministic: the same sites, counts
    /// and run seed always give the same picks, so a run is reproducible from its gem.
    ///
    /// Rules:
    /// <list type="bullet">
    /// <item>A gate pick needs a site of kind gate whose gate model is a door or a barrier (a resident site is
    /// skipped for now: the server cannot re-lock an existing door yet).</item>
    /// <item>A type is eligible at a site only when the site lists it AND the site can hold it: sigil and odd need
    /// maxN of at least 4 (their lever count is 4 or 5), beam needs maxN of at least 2, shuffle needs at least
    /// 2 shuffle spots.</item>
    /// <item>No type is used twice in one run, gate and reward picks together.</item>
    /// <item>Up to <c>gateCount</c> gate sites, drawn in a seeded shuffle of the file order; then, when
    /// <c>wantReward</c>, the first reward site in file order that still has an unused eligible type.</item>
    /// </list>
    /// Every draw comes from one xorshift stream (SkyDecorRandom), never System.Random, whose seeded sequence is
    /// a framework implementation detail.
    /// </summary>
    public static class ThreadPuzzleSitePicker
    {
        /// <summary>Upper bound on gate puzzles per run, whatever the tunable says.</summary>
        public const int MaxGatesPerRun = 8;

        /// <summary>Beam lever count: the site's maxN, capped at the admin default.</summary>
        public const int BeamN = PuzzleGateTunables.DefaultN;

        /// <summary>Is the site's gate model one the server can place today? A reward site needs no model (its gate is the focal object).</summary>
        public static bool IsPlaceable(PuzzleSiteDef site)
        {
            if (site?.Anchor == null)
                return false;

            if (site.Kind == PuzzleSiteKind.Reward)
                return true;

            return site.GateModel != null && site.GateModel.Kind != PuzzleGateModelKind.Resident && site.GateModel.Wcid != 0;
        }

        /// <summary>The types this site lists AND can hold, in file order.</summary>
        public static List<PuzzleGateType> EligibleTypes(PuzzleSiteDef site)
        {
            var result = new List<PuzzleGateType>();

            if (site?.Types == null)
                return result;

            foreach (var type in site.Types)
            {
                if (!result.Contains(type) && Fits(site, type))
                    result.Add(type);
            }

            return result;
        }

        private static bool Fits(PuzzleSiteDef site, PuzzleGateType type)
        {
            switch (type)
            {
                case PuzzleGateType.Sigil:
                case PuzzleGateType.Odd:
                    return site.MaxN >= PuzzleGateTunables.MinPickN;
                case PuzzleGateType.Beam:
                    return site.MaxN >= PuzzleGateTunables.MinN;
                case PuzzleGateType.Shuffle:
                    return (site.ShuffleSpots?.Count ?? 0) >= PuzzleGateTunables.MinSpots;
                default:
                    return false;
            }
        }

        public static List<ThreadPuzzlePick> Pick(IReadOnlyList<PuzzleSiteDef> sites, int gateCount, bool wantReward, int runSeed)
        {
            var picks = new List<ThreadPuzzlePick>();

            if (sites == null || sites.Count == 0)
                return picks;

            gateCount = Math.Clamp(gateCount, 0, MaxGatesPerRun);

            SkyDecorRandom rng;

            unchecked
            {
                // The same mix the generator uses, with a different constant so a run's site stream and its first
                // placement's layout stream never coincide.
                rng = new SkyDecorRandom((uint)runSeed * 2654435761u ^ 0x5A17E5EDu);
            }

            for (var i = 0; i < 4; i++)
                rng.NextUInt();

            var used = new HashSet<PuzzleGateType>();

            // ---- gate sites: a seeded shuffle of the placeable ones, file order as the base ----
            // A disabled site (the kill switch) is never a candidate, so it never consumes the shuffle either.
            var gates = sites.Where(s => s != null && s.Kind == PuzzleSiteKind.Gate && s.UsableAsGate && IsPlaceable(s)).ToList();

            for (var i = gates.Count - 1; i >= 1; i--)
            {
                var j = rng.NextInt(i + 1);
                (gates[i], gates[j]) = (gates[j], gates[i]);
            }

            foreach (var site in gates)
            {
                if (picks.Count >= gateCount)
                    break;

                // Spatial exclusion BEFORE the type draw, so a skipped site never consumes the stream.
                if (TooCloseToAny(site, picks))
                    continue;

                if (TryPickType(site, used, ref rng, out var type, out var n, out var seed))
                    picks.Add(new ThreadPuzzlePick(site, type, n, seed, false));
            }

            // ---- the reward site: the first one in file order that still has an unused type ----
            if (wantReward)
            {
                foreach (var site in sites)
                {
                    if (site == null || site.Kind != PuzzleSiteKind.Reward || !site.UsableAsReward || !IsPlaceable(site))
                        continue;

                    if (TooCloseToAny(site, picks))
                        continue;

                    if (TryPickType(site, used, ref rng, out var type, out var n, out var seed))
                    {
                        picks.Add(new ThreadPuzzlePick(site, type, n, seed, true));
                        break;
                    }
                }
            }

            return picks;
        }

        // ---- spatial exclusion ----------------------------------------------------------------------------

        /// <summary>
        /// Minimum clearance, metres, between two picked sites' footprints (each the segment from its anchor to its
        /// gate). Derived, not tuned: another puzzle's footprint must stay outside this one's approach-prompt radius
        /// (PromptRadius 12, measured from the lever-row centre) widened by the half-width of the widest lever row
        /// (LeverSpacing * (MaxN - 1) / 2 = 3), plus 1 m of margin = 16 m. Closer than that, a player standing at
        /// one puzzle is prompted by, and can reach the levers of, the other.
        /// </summary>
        public static readonly float SiteExclusionMeters =
            PuzzleGateTunables.PromptRadius + PuzzleGateTunables.LeverSpacing * (PuzzleGateTunables.MaxN - 1) / 2.0f + 1.0f;

        /// <summary>Two sites are on different levels (and never excluded) when their anchors differ in height by more than this, metres.</summary>
        public const float SameLevelHeight = 3.0f;

        private static bool TooCloseToAny(PuzzleSiteDef site, List<ThreadPuzzlePick> picks)
        {
            foreach (var pick in picks)
                if (TooClose(site, pick.Site))
                    return true;

            return false;
        }

        /// <summary>
        /// True when the two sites are on the same level (anchor heights within <see cref="SameLevelHeight"/>) and
        /// their anchor-to-gate segments come within <see cref="SiteExclusionMeters"/> of each other in plan view.
        /// Coordinates are landblock-local, so this only compares sites of one dungeon (one landblock), which is all
        /// the picker ever sees.
        /// </summary>
        public static bool TooClose(PuzzleSiteDef a, PuzzleSiteDef b)
        {
            if (a?.Anchor == null || b?.Anchor == null)
                return false;

            if (Math.Abs(a.Anchor.Z - b.Anchor.Z) > SameLevelHeight)
                return false;

            Footprint(a, out var a0, out var a1);
            Footprint(b, out var b0, out var b1);

            return SegmentDistance(a0, a1, b0, b1) < SiteExclusionMeters;
        }

        /// <summary>A site's plan-view footprint: its anchor and its gate point (anchor + forward * gate distance, forward = (-sin yaw, cos yaw)).</summary>
        public static void Footprint(PuzzleSiteDef site, out System.Numerics.Vector2 anchor, out System.Numerics.Vector2 gate)
        {
            anchor = new System.Numerics.Vector2(site.Anchor.X, site.Anchor.Y);
            var distance = (site.Layout ?? PuzzleLayoutParams.Default).GateDistance;
            var yaw = site.Yaw * Math.PI / 180.0;
            gate = anchor + new System.Numerics.Vector2((float)-Math.Sin(yaw), (float)Math.Cos(yaw)) * distance;
        }

        /// <summary>Shortest distance between two 2D segments (0 when they cross).</summary>
        public static float SegmentDistance(System.Numerics.Vector2 p0, System.Numerics.Vector2 p1, System.Numerics.Vector2 q0, System.Numerics.Vector2 q1)
        {
            if (SegmentsIntersect(p0, p1, q0, q1))
                return 0f;

            return Math.Min(Math.Min(PointSegment(p0, q0, q1), PointSegment(p1, q0, q1)),
                            Math.Min(PointSegment(q0, p0, p1), PointSegment(q1, p0, p1)));
        }

        private static float PointSegment(System.Numerics.Vector2 p, System.Numerics.Vector2 a, System.Numerics.Vector2 b)
        {
            var ab = b - a;
            var len2 = ab.LengthSquared();
            var t = len2 <= 1e-12f ? 0f : Math.Clamp(System.Numerics.Vector2.Dot(p - a, ab) / len2, 0f, 1f);
            return System.Numerics.Vector2.Distance(p, a + ab * t);
        }

        private static bool SegmentsIntersect(System.Numerics.Vector2 p0, System.Numerics.Vector2 p1, System.Numerics.Vector2 q0, System.Numerics.Vector2 q1)
        {
            static float Cross(System.Numerics.Vector2 o, System.Numerics.Vector2 a, System.Numerics.Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

            var d1 = Cross(q0, q1, p0);
            var d2 = Cross(q0, q1, p1);
            var d3 = Cross(p0, p1, q0);
            var d4 = Cross(p0, p1, q1);

            // Proper crossings only; touching and collinear cases are covered by the endpoint distances (which are 0 there).
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        /// <summary>
        /// One type draw for one site from its own stream (the arming placer's: a site chosen by player position
        /// rather than by the run's shared stream). Same rules as the run picker: an eligible type the run has not
        /// used, lever count by <see cref="LeverCount"/>, a non-zero placement seed.
        /// </summary>
        public static bool TryPickTypeForSite(PuzzleSiteDef site, IEnumerable<PuzzleGateType> usedTypes, uint streamSeed, bool isReward, out ThreadPuzzlePick pick)
        {
            pick = default;

            if (site == null)
                return false;

            var used = new HashSet<PuzzleGateType>(usedTypes ?? Enumerable.Empty<PuzzleGateType>());
            var rng = new SkyDecorRandom(streamSeed);

            for (var i = 0; i < 4; i++)
                rng.NextUInt();

            if (!TryPickType(site, used, ref rng, out var type, out var n, out var seed))
                return false;

            pick = new ThreadPuzzlePick(site, type, n, seed, isReward);
            return true;
        }

        private static bool TryPickType(PuzzleSiteDef site, HashSet<PuzzleGateType> used, ref SkyDecorRandom rng, out PuzzleGateType type, out int n, out int seed)
        {
            type = default;
            n = 0;
            seed = 0;

            var available = EligibleTypes(site).Where(t => !used.Contains(t)).ToList();

            if (available.Count == 0)
                return false;

            type = available[rng.NextInt(available.Count)];

            // Always two more draws, whatever the type, so the stream after this site never depends on which type won.
            var nRoll = rng.NextUInt();
            seed = (int)(rng.NextUInt() & 0x7FFFFFFFu);

            if (seed == 0)
                seed = 1;

            n = LeverCount(type, site.MaxN, nRoll);
            used.Add(type);
            return true;
        }

        /// <summary>
        /// The lever count for a type at a site of the given maxN. Sigil and odd: 4 or 5 from the roll, never above
        /// maxN (a maxN of 4 always gives 4). Beam: maxN capped at <see cref="BeamN"/>. Shuffle: 1.
        /// </summary>
        public static int LeverCount(PuzzleGateType type, int maxN, uint roll)
        {
            switch (type)
            {
                case PuzzleGateType.Sigil:
                case PuzzleGateType.Odd:
                {
                    var hi = Math.Min(PuzzleGateTunables.MaxPickN, maxN);
                    var lo = PuzzleGateTunables.MinPickN;
                    return hi <= lo ? lo : lo + (int)(roll % (uint)(hi - lo + 1));
                }

                case PuzzleGateType.Beam:
                    return Math.Clamp(Math.Min(maxN, BeamN), PuzzleGateTunables.MinN, PuzzleGateTunables.MaxN);

                default:
                    return 1;
            }
        }

        /// <summary>The placement options for a pick, through the same validated parser the admin command uses.</summary>
        public static bool TryBuildOptions(ThreadPuzzlePick pick, out PuzzleGateOptions options, out string error)
        {
            var tokens = new List<string> { pick.Type.ToString().ToLowerInvariant() };

            if (pick.Type != PuzzleGateType.Shuffle)
                tokens.Add("n=" + pick.N.ToString(CultureInfo.InvariantCulture));

            return PuzzleGateOptions.TryParse(tokens, out options, out error);
        }
    }
}
