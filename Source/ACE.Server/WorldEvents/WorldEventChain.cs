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
            var line = string.IsNullOrWhiteSpace(template) ? "{Family} regroup near {location}! They strike in {seconds} seconds." : template;
            var family = string.IsNullOrWhiteSpace(familyDisplayName) ? "the enemy" : familyDisplayName.Trim();

            return line.Replace("{Family}", Capitalize(family))
                       .Replace("{family}", family)
                       .Replace("{location}", string.IsNullOrWhiteSpace(location) ? "here" : location.Trim())
                       .Replace("{seconds}", Math.Max(0, seconds).ToString());
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

            return null;
        }

        /// <summary>
        /// The next stage's request (D6 - pure): every axis rolled at random, at <paramref name="anchor"/>, with the lead
        /// times of the countdown. The first stage's test settings (--level, --abandon) carry over, so a staff test chains
        /// the same way; a forced wave type does not.
        /// </summary>
        public static WorldEventRequest NextRequest(WorldEventRequest previous, ChainDef chain, int stage, int maxLength, uint rootRunId,
            uint previousRunId, Position anchor, string anchorLabel)
        {
            var leads = Leads(chain?.CountdownSeconds ?? 120, WorldEvent.DefaultAnnounceLeadSeconds);

            return new WorldEventRequest
            {
                Random = true,
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
