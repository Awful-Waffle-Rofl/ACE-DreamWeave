using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Where the reward scene goes at arming, and why. <see cref="Pick"/>.IsReward is always true. <see cref="Walk"/> is
    /// the clearing player's walk to the anchor when the walk rule chose it (null otherwise); <see cref="Rule"/> is
    /// "walk" or "straight"; <see cref="WalkNote"/> says why a walk measure, offered, chose nothing ("none-reachable").
    /// </summary>
    public sealed record ThreadRewardArmingChoice(ThreadPuzzlePick Pick, bool GateAsReward, bool SameFloor, float Distance, bool CuratedFallback)
    {
        public float? Walk { get; init; }

        public string Rule { get; init; } = ThreadPuzzleRewardSelector.RuleStraight;

        public string WalkNote { get; init; }
    }

    /// <summary>
    /// Chooses the reward scene's site at ARMING, near the clearing player. PURE: no world, no PropertyManager.
    /// Deterministic from the run seed apart from the player-position input and the walk measure.
    ///
    /// "Near" means on foot whenever the walk can be measured (the dungeon's cell-portal graph, DungeonWalkGraph):
    /// <list type="number">
    /// <item>Candidate pool: the dungeon's reward sites plus its gate sites (a gate site stands in with the reward
    /// focal object instead of its door). Excluded: a resident-door gate site (its door is still standing in the
    /// doorway), a site this run already placed a gate puzzle at, any site within the picker's spacing of a placed
    /// gate (<see cref="ThreadPuzzleSitePicker.TooClose"/>), and a site with no eligible type the run's gates have
    /// not already used.</item>
    /// <item>WALK rule (a walk measure was supplied): the candidate with the shortest walk from the player wins; a
    /// candidate the walk cannot reach (another cell graph, or behind a closed gate) is excluded. No floor preference:
    /// the walk already prices stairs.</item>
    /// <item>STRAIGHT rule (no walk measure, or no candidate reachable on foot): same floor first - candidates whose
    /// anchor height is within <see cref="ThreadPuzzleSitePicker.SameLevelHeight"/> of the player's - then the nearest
    /// by straight-line (3D) distance.</item>
    /// <item>No player position, or an empty pool: the curated reward pick the pass planned at populate.</item>
    /// </list>
    /// Ties break on file order, so the result never depends on dictionary or hash order.
    /// </summary>
    public static class ThreadPuzzleRewardSelector
    {
        public const string RuleWalk = "walk";

        public const string RuleStraight = "straight";

        /// <param name="walk">
        /// The walk from the clearing player to a point in a cell (the site anchor's cell and position), metres; null
        /// for an unreachable point. Pass null when no walk can be measured at all: the straight rule then applies.
        /// </param>
        /// <param name="behindClosedGate">
        /// True for a point the walk reaches only through one of this run's closed gates. The straight-line rule skips
        /// such a site, so a fallback taken because nothing was reachable on foot never chooses a site behind a gate.
        /// Null: nothing is behind a gate.
        /// </param>
        public static ThreadRewardArmingChoice Select(IReadOnlyList<PuzzleSiteDef> sites, IReadOnlyList<ThreadPuzzlePick> placedGates, ThreadPuzzlePick? curated,
            int runSeed, Vector3? player, Func<uint, Vector3, float?> walk = null, Func<uint, Vector3, bool> behindClosedGate = null)
        {
            placedGates ??= Array.Empty<ThreadPuzzlePick>();
            var usedTypes = placedGates.Select(g => g.Type).ToList();
            string walkNote = null;

            if (player.HasValue && sites != null)
            {
                var p = player.Value;

                if (walk != null)
                {
                    ThreadPuzzlePick? nearest = null;
                    var nearestWalk = float.MaxValue;

                    foreach (var site in sites)
                    {
                        if (!IsCandidate(site, placedGates))
                            continue;

                        var anchor = AnchorOf(site);
                        var w = walk(site.Anchor.Cell, anchor);

                        if (!w.HasValue || !float.IsFinite(w.Value))
                            continue; // unreachable on foot: excluded

                        if (!ThreadPuzzleSitePicker.TryPickTypeForSite(site, usedTypes, StreamSeed(runSeed, site.Id), true, out var pick))
                            continue;

                        if (nearest == null || w.Value < nearestWalk)
                        {
                            nearest = pick;
                            nearestWalk = w.Value;
                        }
                    }

                    if (nearest.HasValue)
                    {
                        var anchor = AnchorOf(nearest.Value.Site);
                        var same = Math.Abs(anchor.Z - p.Z) <= ThreadPuzzleSitePicker.SameLevelHeight;
                        return new ThreadRewardArmingChoice(nearest.Value, nearest.Value.Site.Kind == PuzzleSiteKind.Gate, same, Vector3.Distance(anchor, p), false)
                        {
                            Walk = nearestWalk,
                            Rule = RuleWalk,
                        };
                    }

                    walkNote = "none-reachable";
                }

                ThreadPuzzlePick? best = null;
                var bestSame = false;
                var bestDistance = float.MaxValue;

                foreach (var site in sites)
                {
                    if (!IsCandidate(site, placedGates))
                        continue;

                    if (behindClosedGate != null && behindClosedGate(site.Anchor.Cell, AnchorOf(site)))
                        continue;

                    if (!ThreadPuzzleSitePicker.TryPickTypeForSite(site, usedTypes, StreamSeed(runSeed, site.Id), true, out var pick))
                        continue;

                    var anchor = AnchorOf(site);
                    var same = Math.Abs(anchor.Z - p.Z) <= ThreadPuzzleSitePicker.SameLevelHeight;
                    var distance = Vector3.Distance(anchor, p);

                    // Same floor beats any other floor; within a class, nearer wins; a tie keeps the earlier site.
                    var better = best == null
                        || (same && !bestSame)
                        || (same == bestSame && distance < bestDistance);

                    if (better)
                    {
                        best = pick;
                        bestSame = same;
                        bestDistance = distance;
                    }
                }

                if (best.HasValue)
                    return new ThreadRewardArmingChoice(best.Value, best.Value.Site.Kind == PuzzleSiteKind.Gate, bestSame, bestDistance, false) { WalkNote = walkNote };
            }

            // The curated pick was made at populate, against the sites loaded THEN. A /dd reload since may have switched
            // its site off, so the fallback reads the site by id from the CURRENT list; an id no longer there is not usable.
            var currentCurated = curated.HasValue ? CurrentSite(sites, curated.Value.Site?.Id) : null;

            if (currentCurated?.Anchor != null && currentCurated.UsableAsReward)
            {
                var c = curated.Value with { Site = currentCurated, IsReward = true };
                var anchor = AnchorOf(c.Site);
                var same = player.HasValue && Math.Abs(anchor.Z - player.Value.Z) <= ThreadPuzzleSitePicker.SameLevelHeight;
                var distance = player.HasValue ? Vector3.Distance(anchor, player.Value) : -1f;
                return new ThreadRewardArmingChoice(c, c.Site.Kind == PuzzleSiteKind.Gate, same, distance, true) { WalkNote = walkNote };
            }

            return null;
        }

        private static Vector3 AnchorOf(PuzzleSiteDef site) => new Vector3(site.Anchor.X, site.Anchor.Y, site.Anchor.Z);

        /// <summary>The site with <paramref name="id"/> in <paramref name="sites"/> (ordinal), or null.</summary>
        private static PuzzleSiteDef CurrentSite(IReadOnlyList<PuzzleSiteDef> sites, string id)
        {
            if (sites == null || id == null)
                return null;

            foreach (var s in sites)
                if (s != null && string.Equals(s.Id, id, StringComparison.Ordinal))
                    return s;

            return null;
        }

        /// <summary>Is the site in the arming pool, before the type draw? See the class summary for each exclusion.</summary>
        public static bool IsCandidate(PuzzleSiteDef site, IReadOnlyList<ThreadPuzzlePick> placedGates)
        {
            if (site?.Anchor == null || string.IsNullOrEmpty(site.Id))
                return false;

            // The kill switch: a disabled site, or one disabled for the reward role, never hosts the scene.
            if (!site.UsableAsReward)
                return false;

            if (site.Kind == PuzzleSiteKind.Gate && site.GateModel?.Kind == PuzzleGateModelKind.Resident)
                return false;

            if (site.Kind != PuzzleSiteKind.Gate && site.Kind != PuzzleSiteKind.Reward)
                return false;

            foreach (var gate in placedGates ?? Array.Empty<ThreadPuzzlePick>())
            {
                if (gate.Site == null)
                    continue;

                if (ReferenceEquals(gate.Site, site) || string.Equals(gate.Site.Id, site.Id, StringComparison.Ordinal))
                    return false;

                if (ThreadPuzzleSitePicker.TooClose(site, gate.Site))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The per-site draw stream: the run seed mixed with a STABLE hash of the site id (FNV-1a; string.GetHashCode
        /// is randomized per process and would make a run irreproducible from its gem).
        /// </summary>
        public static uint StreamSeed(int runSeed, string siteId)
        {
            unchecked
            {
                var h = 2166136261u;

                foreach (var ch in siteId ?? string.Empty)
                {
                    h ^= ch;
                    h *= 16777619u;
                }

                return ((uint)runSeed * 2654435761u) ^ h ^ 0xA5E1D0C3u;
            }
        }

        // ---- the boss's chamber --------------------------------------------------------------------------

        /// <summary>A scene within this walk of the dungeon's boss anchor, on its floor, is in the boss's chamber (metres).</summary>
        public const float BossChamberWalkMeters = 15.0f;

        /// <summary>
        /// Is the reward scene in the boss's chamber? Yes when its anchor is in the boss anchor's cell, or within
        /// <see cref="BossChamberWalkMeters"/> on foot of the boss anchor with less than
        /// <see cref="VerticalClauseMeters"/> between their heights. Never without a boss anchor. With no walk measure
        /// only the same-cell clause can say yes.
        /// </summary>
        public static bool IsBossChamber(uint anchorCell, Vector3 anchor, uint? bossCell, Vector3? boss, float? walkToBoss)
        {
            if (!bossCell.HasValue || !boss.HasValue)
                return false;

            if (anchorCell != 0 && anchorCell == bossCell.Value)
                return true;

            return walkToBoss.HasValue
                && walkToBoss.Value <= BossChamberWalkMeters
                && Math.Abs(anchor.Z - boss.Value.Z) < VerticalClauseMeters;
        }

        // ---- the armed line ------------------------------------------------------------------------------

        /// <summary>Within this many metres of the anchor (on foot when known) the armed line says the scene forms beside the member.</summary>
        public const float BesideMeters = 5.0f;

        /// <summary>Under this horizontal offset a scene well above or below reads "directly above / below you" (metres).</summary>
        public const float OverheadHorizontalMeters = 2.0f;

        /// <summary>From this height difference on the line carries a vertical clause (metres).</summary>
        public const float VerticalClauseMeters = 3.0f;

        /// <summary>
        /// The armed line for one recipient: directional when the recipient stands in the run's copy and the scene's
        /// anchor is known, otherwise the plain line (a solo owner messaged from outside the copy, a run whose scene
        /// was placed before arming without an anchor to hand, a recipient with no position).
        /// </summary>
        public static string ArmedLineForRecipient(bool inRunCopy, Vector3? member, Vector3? anchor, float? walk = null, bool bossChamber = false)
            => inRunCopy && member.HasValue && anchor.HasValue
                ? ArmedLineFor(member.Value, anchor.Value, walk, bossChamber)
                : PuzzleGateText.RewardArmed;

        /// <summary>
        /// The armed line for a member at <paramref name="member"/>, pointing at the reward scene's anchor. Both are
        /// landblock-local; +Y is north and +X east (LandblockId.North/East, MlDigsiteMechanicGeometry.Compass).
        /// <paramref name="walk"/> is THIS member's walk to the anchor; without it the metres are a straight line and
        /// the line drops "on foot".
        /// </summary>
        public static string ArmedLineFor(Vector3 member, Vector3 anchor, float? walk = null, bool bossChamber = false)
        {
            var offset = anchor - member;
            var straight = offset.Length();

            var onFoot = walk.HasValue && float.IsFinite(walk.Value);
            var distance = onFoot ? walk.Value : straight;

            if (!float.IsFinite(distance) || distance <= BesideMeters)
                return PuzzleGateText.RewardArmedBeside;

            var horizontal = MathF.Sqrt(offset.X * offset.X + offset.Y * offset.Y);
            var dz = offset.Z;
            var vertical = Math.Abs(dz) >= VerticalClauseMeters;

            string where;
            string verticalClause = "";

            if (horizontal < OverheadHorizontalMeters && vertical)
            {
                // Straight overhead or underfoot: the vertical is the direction, so no separate vertical clause.
                where = dz > 0 ? PuzzleGateText.RewardArmedDirectlyAbove : PuzzleGateText.RewardArmedDirectlyBelow;
            }
            else if (horizontal < OverheadHorizontalMeters)
            {
                // Within a couple of metres on the member's own floor, yet more than BesideMeters away (so this is a
                // walk: the far side of a wall): no compass word means anything at that offset.
                where = PuzzleGateText.RewardArmedNearby;
            }
            else
            {
                var direction = ACE.Server.MlDigsite.MlDigsiteMechanicGeometry.Compass(offset.X, offset.Y);
                where = PuzzleGateText.RewardArmedCompass(direction);

                if (vertical)
                    verticalClause = PuzzleGateText.RewardArmedVertical(Whole(Math.Abs(dz)), dz > 0);
            }

            return PuzzleGateText.RewardArmedToward(where, Whole(distance), onFoot, verticalClause, bossChamber);
        }

        private static int Whole(float metres) => (int)Math.Round(metres, MidpointRounding.AwayFromZero);
    }
}
