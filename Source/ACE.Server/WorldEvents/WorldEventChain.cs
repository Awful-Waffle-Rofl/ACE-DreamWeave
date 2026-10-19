using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;

using ACE.Entity;
using ACE.Server.Entity;
using LScape = ACE.Server.Physics.Common.LScape;
using LandCell = ACE.Server.Physics.Common.LandCell;
using LandDefs = ACE.Server.Physics.Common.LandDefs;
using Position = ACE.Entity.Position;
using ACE.Server.WorldEvents.Defs;

using log4net;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// goals.json "chain": when a run of the goal succeeds, a second World Event starts at a different spot nearby, with a
    /// random compatible source, family and goal, after a countdown broadcast with its location. Only a success chains;
    /// a failed or aborted stage ends the chain, and it never runs longer than <see cref="MaxLength"/> stages.
    /// </summary>
    public class ChainDef
    {
        /// <summary>The most stages in one chain, the first included (2 to 5). Default 2.</summary>
        [JsonPropertyName("maxLength")]
        public int MaxLength { get; set; } = 2;

        /// <summary>Seconds from the broadcast to the next stage's start (60 to 600). Default 120.</summary>
        [JsonPropertyName("countdownSeconds")]
        public int CountdownSeconds { get; set; } = 120;

        /// <summary>The next stage is at least this far from the last one's centre, in metres (100 to 2000). Default 250.</summary>
        [JsonPropertyName("minDistance")]
        public double MinDistance { get; set; } = 250;

        /// <summary>And at most this far, in metres (above minDistance, up to 3000). Default 500.</summary>
        [JsonPropertyName("maxDistance")]
        public double MaxDistance { get; set; } = 500;

        /// <summary>
        /// The World Event broadcast, one picked at random: tokens {family}, {Family} (the next stage's family), {location}
        /// (nearest town and map coordinates) and {seconds}. Without lines: "{Family} regroup near {location}! They strike
        /// again in {seconds} seconds."
        /// </summary>
        [JsonPropertyName("announce")]
        public List<string> Announce { get; set; } = new List<string>();

        /// <summary>
        /// The next stage goes to a different town of the Town Network (WorldEventTownIndex), never the one the last stage
        /// was nearest: one of the <see cref="TownChoices"/> nearest towns that lie between townMinDistance and
        /// townMaxDistance from the last stage. True by default; false keeps the old "250 to 500 m away" spot.
        /// </summary>
        [JsonPropertyName("toAnotherTown")]
        public bool ToAnotherTown { get; set; } = true;

        /// <summary>A town counts as different only this far (metres) from the last stage, so one town's two portals are never two towns (200 to 5000). Default 800.</summary>
        [JsonPropertyName("townMinDistance")]
        public double TownMinDistance { get; set; } = 800;

        /// <summary>The farthest town to send players to, in metres (above townMinDistance, up to 30000). Default 8000.</summary>
        [JsonPropertyName("townMaxDistance")]
        public double TownMaxDistance { get; set; } = 8000;

        /// <summary>How many of the nearest eligible towns the pick is made from (1 to 10). Default 4.</summary>
        [JsonPropertyName("townChoices")]
        public int TownChoices { get; set; } = 4;

        /// <summary>The event stands this far from the town's portal drop, in metres: on open ground at its edge, not among its buildings (20 to 400). Default 60.</summary>
        [JsonPropertyName("townSpotMinDistance")]
        public double TownSpotMinDistance { get; set; } = 60;

        /// <summary>And at most this far (above townSpotMinDistance, up to 600). Default 160.</summary>
        [JsonPropertyName("townSpotMaxDistance")]
        public double TownSpotMaxDistance { get; set; } = 160;

        /// <summary>
        /// The reward profile (rewards.json id) each stage pays, first stage first; a stage past the end of the list uses
        /// the last entry. Each stage pays its own cache to the players who fought in it. Empty: every stage pays the
        /// default profile. The envoys: "standard" for stage 1, "chain_final" for stage 2.
        /// </summary>
        [JsonPropertyName("stageRewards")]
        public List<string> StageRewards { get; set; } = new List<string>();

        /// <summary>
        /// Said locally at the finished stage as the chain moves on, one at random, so nobody leaves its cache behind: token
        /// {town}. Without lines: "Your reward for this fight waits in the cache. Claim it before you go to {town}!".
        /// </summary>
        [JsonPropertyName("claimReminder")]
        public List<string> ClaimReminder { get; set; } = new List<string>();

        /// <summary>The value that rolls an axis at random, as before 2026-10-19.</summary>
        public const string RandomAxis = "random";

        public const string DefaultNextSource = "sky_rift";
        public const string DefaultNextGoal = "kill_boss";
        public const string DefaultNextBoss = "auto";

        /// <summary>
        /// The source every later stage uses (sources.json id), or "random". Default "sky_rift": the second stage is a
        /// rift that drops a champion.
        /// </summary>
        [JsonPropertyName("nextSource")]
        public string NextSource { get; set; } = DefaultNextSource;

        /// <summary>The goal every later stage uses (goals.json id), or "random". Default "kill_boss": slay the champion.</summary>
        [JsonPropertyName("nextGoal")]
        public string NextGoal { get; set; } = DefaultNextGoal;

        /// <summary>
        /// The boss of every later stage (bosses.json id, "auto" for a named boss of the rolled family, "family_champion"),
        /// or "random". Default "auto". Named bosses spawn at the World Events boss scale.
        /// </summary>
        [JsonPropertyName("nextBoss")]
        public string NextBoss { get; set; } = DefaultNextBoss;

        /// <summary>An axis value as the request takes it: null for "random" or blank, else the trimmed lowercase id.</summary>
        public static string Pinned(string axis)
        {
            if (string.IsNullOrWhiteSpace(axis))
                return null;

            var id = axis.Trim().ToLowerInvariant();

            return id == RandomAxis ? null : id;
        }

        /// <summary>The reward id for <paramref name="stage"/> (1-based), or null when the list is empty.</summary>
        public string RewardForStage(int stage)
        {
            if (StageRewards == null || StageRewards.Count == 0)
                return null;

            var id = StageRewards[Math.Clamp(stage - 1, 0, StageRewards.Count - 1)];

            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }
    }
}

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The World Events chain (2026-10-18): a successful run whose goal carries "chain" hands over to a second run nearby.
    /// The rules are pure and tested; WorldEventManager applies them (retiring the finished run into its claim window
    /// while the next one counts down) and <see cref="TryPickNearbyAnchor"/> finds the spot.
    /// </summary>
    public static class WorldEventChain
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventChain));

        /// <summary>What the manager does when a run ends (D6 - pure).</summary>
        public enum Decision
        {
            /// <summary>The run's goal has no chain, and it is not part of one.</summary>
            None,

            /// <summary>Start the next stage.</summary>
            Next,

            /// <summary>Part of a chain, but it failed or was aborted: the chain ends.</summary>
            EndedByOutcome,

            /// <summary>Part of a chain that reached its last stage: the chain ends.</summary>
            EndedAtMax
        }

        /// <summary>
        /// The chain rule (D6 - pure): a <see cref="WorldEventOutcome.Success"/> with a chain (the run's own goal, or the
        /// chain it is a stage of: <paramref name="maxLength"/> above 0) and stages left starts the next; anything else
        /// ends it.
        /// </summary>
        public static Decision Decide(WorldEventOutcome outcome, int stage, int maxLength)
        {
            if (maxLength <= 1)
                return Decision.None;

            if (outcome != WorldEventOutcome.Success)
                return Decision.EndedByOutcome;

            return stage >= maxLength ? Decision.EndedAtMax : Decision.Next;
        }

        /// <summary>The chain length a run follows (D6 - pure): the one it inherited as a later stage, else its goal's, else none (0).</summary>
        public static int EffectiveMaxLength(int inherited, ChainDef goalChain)
        {
            if (inherited > 0)
                return inherited;

            return goalChain != null ? Math.Clamp(goalChain.MaxLength, 2, 5) : 0;
        }

        /// <summary>
        /// The next stage's lead times (D6 - pure): the teaser then the announce, adding up to the countdown, so the run
        /// goes Active <paramref name="countdownSeconds"/> after the broadcast.
        /// </summary>
        public static (int Teaser, int Announce) Leads(int countdownSeconds, int announceLead)
        {
            var countdown = Math.Max(0, countdownSeconds);
            var announce = Math.Clamp(announceLead, 0, countdown);

            return (countdown - announce, announce);
        }

        /// <summary>A candidate offset (D6 - pure): a random bearing, a distance between the two bounds, in metres east and north.</summary>
        public static (double Dx, double Dy) Offset(Random rng, double minDistance, double maxDistance)
        {
            rng ??= new Random();

            var lo = Math.Max(0, Math.Min(minDistance, maxDistance));
            var hi = Math.Max(lo, Math.Max(minDistance, maxDistance));

            var bearing = rng.NextDouble() * 2 * Math.PI;
            var distance = lo + rng.NextDouble() * (hi - lo);

            return (Math.Sin(bearing) * distance, Math.Cos(bearing) * distance);
        }

        /// <summary>The broadcast (D6 - pure): {family}, {Family}, {location} and {seconds} filled; the default when there is no template.</summary>
        public static string AnnounceLine(string template, string familyDisplayName, string location, int seconds)
        {
            return AnnounceLine(template, familyDisplayName, location, seconds, null);
        }

        /// <summary>
        /// <see cref="AnnounceLine(string, string, string, int)"/> with {town}, the next stage's town by name (the location's
        /// own name when no town was picked).
        /// </summary>
        public static string AnnounceLine(string template, string familyDisplayName, string location, int seconds, string town)
        {
            return AnnounceLine(template, familyDisplayName, location, seconds, town, null);
        }

        /// <summary>With {boss}, the next stage's champion by name ("their champion" when it has none).</summary>
        public static string AnnounceLine(string template, string familyDisplayName, string location, int seconds, string town, string boss)
        {
            var line = string.IsNullOrWhiteSpace(template) ? "{Family} regroup near {location}! They strike in {seconds} seconds." : template;
            var family = string.IsNullOrWhiteSpace(familyDisplayName) ? "the enemy" : familyDisplayName.Trim();
            var place = string.IsNullOrWhiteSpace(town) ? (string.IsNullOrWhiteSpace(location) ? "a town nearby" : location.Trim()) : town.Trim();

            return line.Replace("{town}", place)
                       .Replace("{boss}", string.IsNullOrWhiteSpace(boss) ? "their champion" : boss.Trim())
                       .Replace("{Family}", Capitalize(family))
                       .Replace("{family}", family)
                       .Replace("{location}", string.IsNullOrWhiteSpace(location) ? "here" : location.Trim())
                       .Replace("{seconds}", Math.Max(0, seconds).ToString());
        }

        /// <summary>The claim reminder (D6 - pure): {town} filled; the default when there is no template.</summary>
        public static string ReminderLine(string template, string town)
        {
            var line = string.IsNullOrWhiteSpace(template) ? "Your reward for this fight waits in the cache. Claim it before you go to {town}!" : template;

            return line.Replace("{town}", string.IsNullOrWhiteSpace(town) ? "the next fight" : town.Trim());
        }

        private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>The goals.json check (D6 - pure). Null when valid.</summary>
        public static string Validate(ChainDef chain)
        {
            if (chain == null)
                return null;

            if (chain.MaxLength < 2 || chain.MaxLength > 5)
                return "has an invalid chain.maxLength (2 to 5)";

            if (chain.CountdownSeconds < 60 || chain.CountdownSeconds > 600)
                return "has an invalid chain.countdownSeconds (60 to 600)";

            if (!double.IsFinite(chain.MinDistance) || !double.IsFinite(chain.MaxDistance) || chain.MinDistance < 100
                || chain.MaxDistance <= chain.MinDistance || chain.MaxDistance > 3000)
                return "has an invalid chain distance (minDistance 100 or more, maxDistance above it and up to 3000)";

            if (!double.IsFinite(chain.TownMinDistance) || !double.IsFinite(chain.TownMaxDistance) || chain.TownMinDistance < 200
                || chain.TownMinDistance > 5000 || chain.TownMaxDistance <= chain.TownMinDistance || chain.TownMaxDistance > 30000)
                return "has an invalid chain town distance (townMinDistance 200 to 5000, townMaxDistance above it and up to 30000)";

            if (chain.TownChoices < 1 || chain.TownChoices > 10)
                return "has an invalid chain.townChoices (1 to 10)";

            if (!double.IsFinite(chain.TownSpotMinDistance) || !double.IsFinite(chain.TownSpotMaxDistance) || chain.TownSpotMinDistance < 20
                || chain.TownSpotMinDistance > 400 || chain.TownSpotMaxDistance <= chain.TownSpotMinDistance || chain.TownSpotMaxDistance > 600)
                return "has an invalid chain town spot distance (townSpotMinDistance 20 to 400, townSpotMaxDistance above it and up to 600)";

            return null;
        }

        /// <summary>
        /// The stage rewards' ids that rewards.json does not define (D6 - pure), for the load check.
        /// </summary>
        public static List<string> UnknownStageRewards(ChainDef chain, IEnumerable<string> rewardIds)
        {
            var known = new HashSet<string>(rewardIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            return (chain?.StageRewards ?? new List<string>()).Where(id => string.IsNullOrWhiteSpace(id) || !known.Contains(id.Trim())).ToList();
        }

        /// <summary>
        /// The next stage's town (D6 - pure): of the towns at least <see cref="ChainDef.TownMinDistance"/> from
        /// <paramref name="originX"/>/<paramref name="originY"/> (global metres) and not named <paramref name="originTown"/>, one of
        /// the <see cref="ChainDef.TownChoices"/> nearest within <see cref="ChainDef.TownMaxDistance"/>, at random; with none in
        /// that range, the nearest one past it. Null when no other town exists. <paramref name="exclude"/> names towns
        /// already tried.
        /// </summary>
        public static WorldEventTown? PickTown(IReadOnlyList<WorldEventTown> towns, double originX, double originY, string originTown,
            ChainDef chain, Random rng, ICollection<string> exclude = null)
        {
            chain ??= new ChainDef();
            rng ??= new Random();

            var others = (towns ?? Array.Empty<WorldEventTown>())
                .Where(t => !string.IsNullOrWhiteSpace(t.Name)
                            && !string.Equals(t.Name, originTown, StringComparison.OrdinalIgnoreCase)
                            && (exclude == null || !exclude.Contains(t.Name)))
                .Select(t => (Town: t, Distance: Math.Sqrt((t.GlobalX - originX) * (t.GlobalX - originX) + (t.GlobalY - originY) * (t.GlobalY - originY))))
                .Where(t => t.Distance >= chain.TownMinDistance)
                .OrderBy(t => t.Distance)
                .ToList();

            if (others.Count == 0)
                return null;

            var inRange = others.Where(t => t.Distance <= chain.TownMaxDistance).Take(Math.Max(1, chain.TownChoices)).ToList();

            if (inRange.Count == 0)
                return others[0].Town;

            return inRange[rng.Next(inRange.Count)].Town;
        }

        /// <summary>
        /// A spot at the edge of <paramref name="town"/> (live: reads the landscape): up to <paramref name="attempts"/> random
        /// spots between chain.townSpotMinDistance and townSpotMaxDistance from its portal drop, the first on open ground.
        /// </summary>
        public static bool TryPickNearTown(WorldEventTown town, Position reference, ChainDef chain, Random rng, out Position anchor, int attempts = 40)
        {
            chain ??= new ChainDef();

            return TryPickNearTown(town, reference, chain.TownSpotMinDistance, chain.TownSpotMaxDistance, rng, out anchor, attempts);
        }

        /// <summary><see cref="TryPickNearTown(WorldEventTown, Position, ChainDef, Random, out Position, int)"/> with explicit distances from the drop.</summary>
        public static bool TryPickNearTown(WorldEventTown town, Position reference, double minDistance, double maxDistance, Random rng,
            out Position anchor, int attempts = 40)
        {
            anchor = null;

            if (reference == null)
                return false;

            for (var i = 0; i < attempts; i++)
            {
                var (dx, dy) = Offset(rng, minDistance, maxDistance);
                var global = new Vector3(town.GlobalX + (float)dx, town.GlobalY + (float)dy, 0);

                if (global.X < 0 || global.Y < 0 || global.X >= 255 * Position.BlockLength || global.Y >= 255 * Position.BlockLength)
                    continue;

                try
                {
                    var candidate = reference.FromGlobal(global);
                    candidate.Instance = reference.Instance;

                    if (!IsOpenGround(candidate))
                        continue;

                    candidate.PositionZ = candidate.GetTerrainZ();
                    anchor = candidate;
                    return true;
                }
                catch (Exception ex)
                {
                    log.Debug($"[WORLDEVENT] chain town spot check threw near {town.Name}", ex);
                }
            }

            return false;
        }

        /// <summary>
        /// The stage settings' load check against the store (D6 - pure): an unknown source, goal or boss, or a goal the
        /// source does not support. Empty when valid.
        /// </summary>
        public static List<string> NextStageProblems(ChainDef chain, IReadOnlyDictionary<string, SourceThemeDef> sources,
            ICollection<string> goalIds, ICollection<string> bossIds)
        {
            var problems = new List<string>();

            if (chain == null)
                return problems;

            var source = ChainDef.Pinned(chain.NextSource);
            var goal = ChainDef.Pinned(chain.NextGoal);
            var boss = ChainDef.Pinned(chain.NextBoss);

            SourceThemeDef sourceDef = null;

            if (source != null && (sources == null || !sources.TryGetValue(source, out sourceDef)))
                problems.Add($"chain.nextSource '{source}' is not in sources.json");

            if (goal != null && (goalIds == null || !goalIds.Contains(goal)))
                problems.Add($"chain.nextGoal '{goal}' is not in goals.json");

            if (sourceDef != null && goal != null && !(sourceDef.CompatibleGoals ?? new List<string>()).Contains(goal))
                problems.Add($"chain.nextSource '{source}' does not support chain.nextGoal '{goal}' (its compatibleGoals)");

            if (boss != null && boss != WorldEventComposer.AutoBossId && boss != "family_champion" && boss != WorldEventComposer.DefaultBossId
                && (bossIds == null || !bossIds.Contains(boss)))
                problems.Add($"chain.nextBoss '{boss}' is not in bosses.json (or auto, family_champion, none, random)");

            return problems;
        }

        /// <summary>
        /// The next stage's request (D6 - pure), at <paramref name="anchor"/>, with the lead times of the countdown. The
        /// source, goal and boss are the chain's nextSource / nextGoal / nextBoss (by default a sky rift whose champion
        /// must be slain); an axis set to "random" rolls, and the family always rolls. The first stage's test settings
        /// (--level, --abandon) carry over, so a staff test chains the same way; a forced wave type does not.
        /// </summary>
        public static WorldEventRequest NextRequest(WorldEventRequest previous, ChainDef chain, int stage, int maxLength, uint rootRunId,
            uint previousRunId, Position anchor, string anchorLabel)
        {
            var leads = Leads(chain?.CountdownSeconds ?? 120, WorldEvent.DefaultAnnounceLeadSeconds);

            return new WorldEventRequest
            {
                Random = true,
                SourceId = chain != null ? ChainDef.Pinned(chain.NextSource) : null,
                GoalId = chain != null ? ChainDef.Pinned(chain.NextGoal) : null,
                BossId = chain != null ? ChainDef.Pinned(chain.NextBoss) : null,
                TownReach = previous?.TownReach,
                AnchorPosition = anchor,
                AnchorLabel = anchorLabel,
                Invoker = $"chain from run {previousRunId}",
                AnnounceLeadSeconds = leads.Announce,
                TeaserLeadSeconds = leads.Teaser,
                MinDurationSeconds = previous?.MinDurationSeconds ?? WorldEvent.DefaultMinDurationSeconds,
                MaxDurationSeconds = previous?.MaxDurationSeconds ?? WorldEvent.DefaultMaxDurationSeconds,
                AbandonAfterSeconds = previous?.AbandonAfterSeconds ?? WorldEvent.DefaultAbandonAfterSeconds,
                WipeGraceSeconds = previous?.WipeGraceSeconds ?? WorldEvent.DefaultWipeGraceSeconds,
                TestLevel = previous?.TestLevel,
                AllowedSourceIds = previous?.AllowedSourceIds,
                RewardId = chain?.RewardForStage(stage),
                ChainStage = stage,
                ChainMaxLength = maxLength,
                ChainRootRunId = rootRunId
            };
        }

        /// <summary>
        /// A spot for the next stage (live: reads the landscape): up to <paramref name="attempts"/> random spots between
        /// chain.minDistance and chain.maxDistance from <paramref name="origin"/>, the first that is outdoors, on land
        /// (no water), not inside a building, on a walkable slope and in the same instance. False, with the reason, when
        /// none is found or the origin is indoors.
        /// </summary>
        public static bool TryPickNearbyAnchor(Position origin, ChainDef chain, Random rng, out Position anchor, out string why, int attempts = 40)
        {
            anchor = null;
            why = null;

            if (origin == null || origin.Indoors)
            {
                why = "the first stage was indoors";
                return false;
            }

            var centre = origin.ToGlobal();

            for (var i = 0; i < attempts; i++)
            {
                var (dx, dy) = Offset(rng, chain?.MinDistance ?? 250, chain?.MaxDistance ?? 500);
                var global = new Vector3(centre.X + (float)dx, centre.Y + (float)dy, centre.Z);

                if (global.X < 0 || global.Y < 0 || global.X >= 255 * Position.BlockLength || global.Y >= 255 * Position.BlockLength)
                    continue;

                Position candidate;

                try
                {
                    candidate = origin.FromGlobal(global);
                    candidate.Instance = origin.Instance;

                    if (!IsOpenGround(candidate))
                        continue;

                    candidate.PositionZ = candidate.GetTerrainZ();
                }
                catch (Exception ex)
                {
                    log.Debug($"[WORLDEVENT] chain spot check threw at offset ({dx:F0}, {dy:F0})", ex);
                    continue;
                }

                anchor = candidate;
                return true;
            }

            why = $"no open ground found in {attempts} tries";
            return false;
        }

        /// <summary>Outdoors, on land, not a building's cell, walkable.</summary>
        private static bool IsOpenGround(Position p)
        {
            if (p == null || p.Indoors)
                return false;

            var cell = LScape.get_landcell(p.GetOutdoorCell(), p.Instance) as LandCell;

            if (cell == null || cell.WaterType != LandDefs.WaterType.NotWater || cell.has_building())
                return false;

            return p.IsWalkable();
        }
    }
}
