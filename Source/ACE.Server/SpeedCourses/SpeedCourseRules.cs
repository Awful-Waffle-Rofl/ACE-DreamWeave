using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Pinnacles;
using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.SpeedCourses
{
    /// <summary>What the trigger state machine tells the host to do with one hazard this tick.</summary>
    public enum SpeedHazardDecision
    {
        Hold,
        Start,
        Stop,
    }

    /// <summary>What the Seal door should do this tick (SpeedCourseRules.DecideSealDoor).</summary>
    public enum SpeedSealDoorDecision
    {
        NoChange,
        Shut,
        Open,
    }

    /// <summary>How a hazard's previous run ended.</summary>
    public enum SpeedHazardEnd
    {
        /// <summary>It has not run yet.</summary>
        None,
        Completed,
        Failed,

        /// <summary>The host stopped it (the trigger condition went away).</summary>
        Stopped,
    }

    /// <summary>Everything the trigger state machine reads, captured by the host once per tick.</summary>
    public readonly struct SpeedHazardSnapshot
    {
        /// <summary>True when at least one runner is inside THIS hazard's region.</summary>
        public bool AnyRunnerInRegion { get; init; }

        public bool ClockRebased { get; init; }

        public bool K1Dead { get; init; }
        public bool K2Dead { get; init; }
        public bool BossSpawned { get; init; }
        public bool BossDead { get; init; }

        /// <summary>Boss health as a 0..1 fraction; meaningful only while the boss is spawned.</summary>
        public double BossHealthFraction { get; init; }

        /// <summary>Seconds on any monotonic clock; Last* are on the same clock.</summary>
        public double Now { get; init; }

        public double? LastStart { get; init; }
        public double? LastEndTime { get; init; }
        public SpeedHazardEnd LastEnd { get; init; }

        /// <summary>True while a run of this hazard is live.</summary>
        public bool Running { get; init; }

        public bool BossAlive => BossSpawned && !BossDead;
    }

    /// <summary>
    /// The pure rules of a speed course: run level, the gem spec, the Seal, puzzle type draws, hazard seeds and
    /// trigger decisions, and where the named creatures stand. No world, no database, no clock.
    /// </summary>
    public static class SpeedCourseRules
    {
        // =====================================================================================
        // Level and gem spec
        // =====================================================================================

        /// <summary>
        /// The level a run is played at. When <paramref name="fixedLevel"/> is above 0 the run is flat at that level
        /// (capped at <see cref="DungeonGemSpec.MaxRunLevel"/>) whatever the player's level or the floors. Otherwise it
        /// is the player's level, raised to the larger of the season floor, the course floor and 1, and capped at
        /// <see cref="DungeonGemSpec.MaxRunLevel"/>.
        /// </summary>
        public static int RunLevel(int playerLevel, int seasonLevelFloor, int courseLevelFloor, int fixedLevel)
        {
            if (fixedLevel > 0)
                return Math.Min(fixedLevel, DungeonGemSpec.MaxRunLevel);

            var floor = Math.Max(Math.Max(seasonLevelFloor, courseLevelFloor), 1);
            floor = Math.Min(floor, DungeonGemSpec.MaxRunLevel);
            return Math.Clamp(playerLevel, floor, DungeonGemSpec.MaxRunLevel);
        }

        /// <summary>The course's gem spec at <paramref name="runLevel"/>: its dungeon, tier, family and seed, no modifiers, bound to the owner (run id 0).</summary>
        public static DungeonGemSpec BuildGemSpec(SpeedCourseDef course, int runLevel, uint ownerGuid)
        {
            if (course == null)
                throw new ArgumentNullException(nameof(course));

            return new DungeonGemSpec(course.DungeonId, runLevel, course.Tier, course.Family, course.Seed, null, 0u, ownerGuid);
        }

        /// <summary>
        /// The population limits a course is built with, from the Threads limits read off the tunables: one creature per
        /// curated point, and forced-family widening on. A course's family is fixed, so a run level where that table has
        /// no in-band trash member must widen down to the family's nearest members (uplifted to the run level) rather
        /// than open the course with no monsters. Threads runs never set the widening.
        /// </summary>
        public static DungeonPopulationLimits CourseLimits(DungeonPopulationLimits threadsLimits, int curatedPoints)
            => threadsLimits.WithMonsterCounts(0, curatedPoints).WithForcedFamilyWidening();

        /// <summary>
        /// The WARN text when a built course plan fields fewer non-boss creatures than the course has curated points (some
        /// corridors would stand empty), or null when it fills them all. A null plan is reported elsewhere (the run logs
        /// an ERROR), so it yields null here. The plan's own notes are carried, since they say why.
        /// </summary>
        public static string ShortPlanWarning(DungeonSpawnPlan plan, int curatedPoints)
        {
            if (plan == null)
                return null;

            var nonBoss = plan.Entries.Count(e => e != null && e.Role != DungeonRole.Boss);

            if (nonBoss >= curatedPoints)
                return null;

            return $"the plan fields {nonBoss} non-boss creature(s) for {curatedPoints} curated point(s); notes: "
                + (plan.Notes.Count == 0 ? "none" : string.Join(" | ", plan.Notes));
        }

        // =====================================================================================
        // The Seal
        // =====================================================================================

        /// <summary>
        /// Whether the contributed <paramref name="tokens"/> open the Seal: the summed weight of the DISTINCT
        /// tokens the course defines reaches SealRequired. Unknown names weigh nothing, a repeat counts once
        /// (the same replace-by-key rule ObjectiveLock applies).
        /// </summary>
        public static bool SealOpens(IEnumerable<string> tokens, SpeedCourseDef course)
        {
            if (course == null || tokens == null)
                return false;

            var sum = 0.0;

            foreach (var token in new HashSet<string>(tokens.Where(t => t != null), StringComparer.Ordinal))
                sum += course.WeightOf(token);

            return sum >= course.SealRequired;
        }

        // =====================================================================================
        // Puzzles
        // =====================================================================================

        /// <summary>The puzzle type names the puzzle-gate system offers (PuzzleGateType), in enum order.</summary>
        public static IReadOnlyList<string> PuzzleTypeNames { get; } = Enum.GetNames(typeof(PuzzleGateType));

        /// <summary>
        /// Draws <paramref name="count"/> puzzle types with no type repeated within the run. The input is
        /// de-duplicated first (ordinal), so a repeated name in the list cannot repeat in the draw; when the list
        /// holds fewer distinct types than <paramref name="count"/>, the result is as long as the list.
        /// </summary>
        public static List<string> PickPuzzleTypes(Random rng, int count, IReadOnlyList<string> types)
        {
            if (rng == null)
                throw new ArgumentNullException(nameof(rng));

            var pool = (types ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToList();
            var picked = new List<string>();

            while (picked.Count < count && pool.Count > 0)
            {
                var i = rng.Next(pool.Count);
                picked.Add(pool[i]);
                pool.RemoveAt(i);
            }

            return picked;
        }

        /// <summary>
        /// Draws one puzzle type per site, no type repeated within the run, each from that site's own allowed
        /// set (a corridor site offers only beam and shuffle; a room site all four). Each site's allowed list is
        /// de-duplicated and shuffled with <paramref name="rng"/>, then a depth-first search in site order takes
        /// the first assignment that keeps every type distinct, backtracking when a later site would be left with
        /// nothing. Returns one type per site in site order, or null when no distinct assignment exists (a site
        /// with no allowed type, or more sites than their allowed sets can cover). Pure.
        /// </summary>
        public static List<string> PickPuzzleTypesPerSite(Random rng, IReadOnlyList<IReadOnlyList<string>> allowedPerSite)
        {
            if (rng == null)
                throw new ArgumentNullException(nameof(rng));

            if (allowedPerSite == null)
                return null;

            var candidates = new List<List<string>>(allowedPerSite.Count);

            foreach (var allowed in allowedPerSite)
            {
                var pool = (allowed ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToList();

                // Fisher-Yates, so which of a site's types is tried first is the draw.
                for (var i = pool.Count - 1; i >= 1; i--)
                {
                    var j = rng.Next(i + 1);
                    (pool[i], pool[j]) = (pool[j], pool[i]);
                }

                candidates.Add(pool);
            }

            var result = new string[candidates.Count];
            var used = new HashSet<string>(StringComparer.Ordinal);

            bool Assign(int site)
            {
                if (site == candidates.Count)
                    return true;

                foreach (var type in candidates[site])
                {
                    if (!used.Add(type))
                        continue;

                    result[site] = type;

                    if (Assign(site + 1))
                        return true;

                    used.Remove(type);
                }

                return false;
            }

            return Assign(0) ? result.ToList() : null;
        }

        // =====================================================================================
        // The runner: when a course run ends
        // =====================================================================================

        /// <summary>
        /// Seconds a registered run may wait for its runner to be bound to the instance (StartSpeedChallenge runs on
        /// arrival, after the portal registered the run) before the run is dropped as never started.
        /// </summary>
        public const double RunnerBindGraceSeconds = 120.0;

        /// <summary>
        /// Whether a course run is over, from the runner's state: null to keep running, else the reason. Offline ends
        /// it (a logout forfeits the speed run at the next login anyway). Bound to this instance keeps it. Not bound
        /// after having been bound means the run finished, was forfeited or the runner died. Never bound past
        /// <paramref name="bindGraceSeconds"/> means the arrival never armed it. Pure.
        /// </summary>
        public static string DecideRunnerEnd(bool online, bool boundHere, bool everBound, double secondsSinceRegistered, double bindGraceSeconds)
        {
            if (!online)
                return "runner offline";

            if (boundHere)
                return null;

            if (everBound)
                return "run finished or forfeited";

            return secondsSinceRegistered > bindGraceSeconds ? "runner never armed in this instance" : null;
        }

        // =====================================================================================
        // Hazards: seed, pct, trigger decisions
        // =====================================================================================

        /// <summary>
        /// The hazard seed for a slot: a fixed function of the season seed and the slot (and the memory retry
        /// <paramref name="attempt"/>, 0 for the first start). Never 0 (0 reads as "no seed" to a caller).
        /// </summary>
        public static int HazardSeed(int seasonSeed, int slotIndex, int attempt = 0)
        {
            unchecked
            {
                var h = (uint)seasonSeed * 2654435761u;
                h ^= (uint)(slotIndex + 1) * 40503u;
                h = (h ^ (h >> 15)) * 2246822519u;
                h ^= (uint)attempt * 3266489917u;
                h = (h ^ (h >> 13)) * 668265263u;
                h ^= h >> 16;

                var seed = (int)(h & 0x7FFFFFFFu);
                return seed == 0 ? 1 : seed;
            }
        }

        /// <summary>The least wait, in seconds, between a Failed hazard run and its restart, whatever retrySeconds says.</summary>
        public const double MinFailedRetrySeconds = 1.0;

        /// <summary>The hazard's pct at <paramref name="runLevel"/>: <see cref="HazardLevelScale.Scaled"/> over the authored values.</summary>
        public static double ScaledPct(SpeedHazardDef hazard, int runLevel)
            => HazardLevelScale.Scaled(runLevel, hazard.PctAuthored, hazard.PctMin, hazard.PctMax);

        /// <summary>
        /// Decides what to do with one hazard this tick.
        /// <list type="bullet">
        /// <item>clock_rebase: Start once the clock is rebased; never Stop (it runs to teardown).</item>
        /// <item>while_in_region: Start when a runner is in the region; Stop when none is; a Completed run restarts at once while someone is still in.</item>
        /// <item>on_named_dead_and_in_region: Start when the named slot is dead and a runner is in the region; Stop when none is. A Completed run is final.</item>
        /// <item>boss_alive_interval: Start when the boss is alive and the interval has passed since the last START (first start at once); Stop when the boss dies.</item>
        /// <item>boss_health_below: Start once the boss is alive and at or below the fraction; a run is not stopped by healing, only by the boss dying. A Completed run is final.</item>
        /// </list>
        /// A Failed run waits <paramref name="retrySeconds"/> (at least <see cref="MinFailedRetrySeconds"/>) since it ended before the next Start;
        /// the caller seeds the retry with <see cref="HazardSeed"/> and attempt + 1. A Stopped run may start again
        /// as soon as its condition returns.
        /// </summary>
        public static SpeedHazardDecision DecideHazard(SpeedHazardTriggerDef trigger, double retrySeconds, in SpeedHazardSnapshot s)
        {
            if (trigger == null)
                return SpeedHazardDecision.Hold;

            var type = trigger.Type;

            if (s.Running)
                return ShouldStop(trigger, s) ? SpeedHazardDecision.Stop : SpeedHazardDecision.Hold;

            if (!StartCondition(trigger, s))
                return SpeedHazardDecision.Hold;

            var interval = string.Equals(type, SpeedHazardTriggers.BossAliveInterval, StringComparison.Ordinal);

            if (interval && s.LastStart.HasValue && s.Now - s.LastStart.Value < trigger.Seconds)
                return SpeedHazardDecision.Hold;

            switch (s.LastEnd)
            {
                case SpeedHazardEnd.None:
                case SpeedHazardEnd.Stopped:
                    return SpeedHazardDecision.Start;

                case SpeedHazardEnd.Failed:
                    // Floored at MinFailedRetrySeconds: a hazard that fails at once must not restart every tick.
                    return s.LastEndTime.HasValue && s.Now - s.LastEndTime.Value < Math.Max(retrySeconds, MinFailedRetrySeconds)
                        ? SpeedHazardDecision.Hold
                        : SpeedHazardDecision.Start;

                case SpeedHazardEnd.Completed:
                    return string.Equals(type, SpeedHazardTriggers.WhileInRegion, StringComparison.Ordinal) || interval
                        ? SpeedHazardDecision.Start
                        : SpeedHazardDecision.Hold;

                default:
                    return SpeedHazardDecision.Hold;
            }
        }

        /// <summary>
        /// The Seal door's lock. Shut exactly once, when the Seal is latched, the boss is spawned and alive, the runner is
        /// inside the lock region and the door is not already shut. Open when the boss is dead and the door is shut. Never
        /// shut while the runner is outside, so a runner who has not yet walked in is not locked out. Pure.
        /// </summary>
        public static SpeedSealDoorDecision DecideSealDoor(bool sealLatched, bool bossSpawned, bool bossDead, bool runnerInRegion, bool currentlyShut)
        {
            if (bossSpawned && bossDead)
                return currentlyShut ? SpeedSealDoorDecision.Open : SpeedSealDoorDecision.NoChange;

            if (currentlyShut)
                return SpeedSealDoorDecision.NoChange;

            return sealLatched && bossSpawned && runnerInRegion ? SpeedSealDoorDecision.Shut : SpeedSealDoorDecision.NoChange;
        }

        private static bool NamedDead(string slot, in SpeedHazardSnapshot s)
        {
            if (string.Equals(slot, SpeedNamedSlots.K1, StringComparison.Ordinal))
                return s.K1Dead;

            if (string.Equals(slot, SpeedNamedSlots.K2, StringComparison.Ordinal))
                return s.K2Dead;

            if (string.Equals(slot, SpeedNamedSlots.Boss, StringComparison.Ordinal))
                return s.BossDead;

            return false;
        }

        private static bool StartCondition(SpeedHazardTriggerDef trigger, in SpeedHazardSnapshot s)
        {
            switch (trigger.Type)
            {
                case SpeedHazardTriggers.ClockRebase: return s.ClockRebased;
                case SpeedHazardTriggers.WhileInRegion: return s.AnyRunnerInRegion;
                case SpeedHazardTriggers.OnNamedDeadAndInRegion: return NamedDead(trigger.Slot, s) && s.AnyRunnerInRegion;
                case SpeedHazardTriggers.BossAliveInterval: return s.BossAlive;
                case SpeedHazardTriggers.BossHealthBelow: return s.BossAlive && s.BossHealthFraction <= trigger.Fraction;
                default: return false;
            }
        }

        private static bool ShouldStop(SpeedHazardTriggerDef trigger, in SpeedHazardSnapshot s)
        {
            switch (trigger.Type)
            {
                case SpeedHazardTriggers.WhileInRegion:
                case SpeedHazardTriggers.OnNamedDeadAndInRegion:
                    return !s.AnyRunnerInRegion;
                case SpeedHazardTriggers.BossAliveInterval:
                case SpeedHazardTriggers.BossHealthBelow:
                    return !s.BossAlive;
                default:
                    return false;
            }
        }

        // =====================================================================================
        // Runner messages
        // =====================================================================================

        /// <summary>
        /// The Seal progress groups as told to the runner, e.g. "ward gates 1 of 2, rites 2 of 2": per group, how many of
        /// its tokens are in <paramref name="counted"/>, capped at the group's "of". Empty when the course has no groups.
        /// </summary>
        public static string ProgressText(SpeedCourseDef course, ICollection<string> counted)
        {
            var parts = new List<string>();

            foreach (var g in course?.ProgressGroups ?? new List<SpeedProgressGroupDef>())
            {
                if (g == null)
                    continue;

                var have = (g.Tokens ?? new List<string>()).Count(t => t != null && counted != null && counted.Contains(t));
                parts.Add($"{g.Name} {Math.Min(have, g.Of)} of {g.Of}");
            }

            return string.Join(", ", parts);
        }

        /// <summary>A message template with {label} and {groups} filled in; null for a null template.</summary>
        public static string FormatMessage(string template, string label, string groups)
            => template?.Replace("{label}", label ?? "").Replace("{groups}", groups ?? "");

        // =====================================================================================
        // Named creatures
        // =====================================================================================

        /// <summary>The result of <see cref="PlaceNamed"/>.</summary>
        public sealed class NamedPlacement
        {
            /// <summary>Every entry of the plan, in the plan's order, with the named ones moved.</summary>
            public List<DungeonSpawnPlanEntry> Entries { get; } = new List<DungeonSpawnPlanEntry>();

            /// <summary>The moved entries (also in <see cref="Entries"/>), null when the plan had none to give.</summary>
            public DungeonSpawnPlanEntry K1 { get; set; }
            public DungeonSpawnPlanEntry K2 { get; set; }
            public DungeonSpawnPlanEntry Boss { get; set; }
        }

        /// <summary>
        /// Moves plan entries onto the K1, K2 and BOSS positions. K1 and K2 are the first two Elite entries in
        /// plan order; when the plan holds fewer than two elites the rest come from the highest-level Trash
        /// entries (<paramref name="levelOf"/>, ties in plan order). The Boss entry (the first) takes the BOSS
        /// position. A moved entry keeps its wcid, uplift and stamp and takes the slot's point; a K1/K2 entry is
        /// always shaped as an Elite (a promoted Trash entry has its role raised), the Boss keeps its role. The
        /// input plan is not modified. A null position leaves that slot's entry where it was (and its role as is).
        /// </summary>
        public static NamedPlacement PlaceNamed(IReadOnlyList<DungeonSpawnPlanEntry> entries,
            DungeonSpawnPointDef k1Point, DungeonSpawnPointDef k2Point, DungeonSpawnPointDef bossPoint,
            Func<DungeonSpawnPlanEntry, int> levelOf)
        {
            var result = new NamedPlacement();

            if (entries == null)
                return result;

            levelOf ??= e => e.RaisedLevel;

            var elites = new List<int>();
            var trash = new List<int>();
            var bossIndex = -1;

            for (var i = 0; i < entries.Count; i++)
            {
                switch (entries[i].Role)
                {
                    case DungeonRole.Elite: elites.Add(i); break;
                    case DungeonRole.Boss: if (bossIndex < 0) bossIndex = i; break;
                    default: trash.Add(i); break;
                }
            }

            var chosen = new List<int>(elites.Take(2));

            if (chosen.Count < 2)
                chosen.AddRange(trash
                    .Select((index, order) => (index, order))
                    .OrderByDescending(t => levelOf(entries[t.index]))
                    .ThenBy(t => t.order)
                    .Take(2 - chosen.Count)
                    .Select(t => t.index));

            var moved = new Dictionary<int, DungeonSpawnPointDef>();

            if (chosen.Count > 0 && k1Point != null)
                moved[chosen[0]] = k1Point;

            if (chosen.Count > 1 && k2Point != null)
                moved[chosen[1]] = k2Point;

            if (bossIndex >= 0 && bossPoint != null)
                moved[bossIndex] = bossPoint;

            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];

                if (moved.TryGetValue(i, out var point))
                {
                    var role = i == bossIndex ? e.Role : DungeonRole.Elite;
                    e = new DungeonSpawnPlanEntry(e.Wcid, role, point, e.UpliftLevel, e.StampLevel);
                }

                result.Entries.Add(e);

                if (chosen.Count > 0 && i == chosen[0] && moved.ContainsKey(i)) result.K1 = e;
                if (chosen.Count > 1 && i == chosen[1] && moved.ContainsKey(i)) result.K2 = e;
                if (i == bossIndex && moved.ContainsKey(i)) result.Boss = e;
            }

            return result;
        }

        // =====================================================================================
        // Door guardians
        // =====================================================================================

        /// <summary>One guardian and the plan clone that stands for it (<see cref="PlaceGuardians"/>).</summary>
        public sealed class GuardianPlacement
        {
            public GuardianPlacement(SpeedGuardianDef guardian, DungeonSpawnPlanEntry entry)
            {
                Guardian = guardian;
                Entry = entry;
            }

            public SpeedGuardianDef Guardian { get; }

            /// <summary>A NEW entry: the source's wcid, uplift and stamp, role Elite, at the guardian's point.</summary>
            public DungeonSpawnPlanEntry Entry { get; }
        }

        /// <summary>
        /// The creature each guardian is, cloned from the plan. Guardian i clones the i-th Elite entry in plan order,
        /// cycling when there are more guardians than elites; a plan with no elites gives every guardian its single
        /// highest-level Trash entry (<paramref name="levelOf"/>, ties in plan order, the PlaceNamed tie-break). A clone
        /// keeps the source's wcid, uplift and stamp level, is shaped as an Elite, and stands at the guardian's point.
        /// <para/>
        /// CLONES, NEVER CONSUMES: the plan's entries are not removed, moved or modified, and no random number is drawn,
        /// so the season's population draw is byte-identical with or without guardians. Empty when the plan has no
        /// non-boss entry; a null guardian, or one with no position, gets no placement (the others keep their index).
        /// </summary>
        public static List<GuardianPlacement> PlaceGuardians(IReadOnlyList<DungeonSpawnPlanEntry> entries,
            IReadOnlyList<SpeedGuardianDef> guardians, Func<DungeonSpawnPlanEntry, int> levelOf)
        {
            var result = new List<GuardianPlacement>();

            if (entries == null || guardians == null || guardians.Count == 0)
                return result;

            levelOf ??= e => e.RaisedLevel;

            var elites = new List<DungeonSpawnPlanEntry>();
            var trash = new List<DungeonSpawnPlanEntry>();

            foreach (var e in entries)
            {
                if (e == null)
                    continue;

                switch (e.Role)
                {
                    case DungeonRole.Elite: elites.Add(e); break;
                    case DungeonRole.Boss: break;
                    default: trash.Add(e); break;
                }
            }

            List<DungeonSpawnPlanEntry> sources;

            if (elites.Count > 0)
                sources = elites;
            else if (trash.Count > 0)
                sources = new List<DungeonSpawnPlanEntry>
                {
                    trash.Select((entry, order) => (entry, order))
                        .OrderByDescending(t => levelOf(t.entry))
                        .ThenBy(t => t.order)
                        .First().entry,
                };
            else
                return result;

            for (var i = 0; i < guardians.Count; i++)
            {
                var g = guardians[i];

                if (g?.Position == null)
                    continue;

                var src = sources[i % sources.Count];
                result.Add(new GuardianPlacement(g, new DungeonSpawnPlanEntry(src.Wcid, DungeonRole.Elite, g.Position, src.UpliftLevel, src.StampLevel)));
            }

            return result;
        }
    }
}