using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The Hold finale boss (2026-10-19): its size, and how it threatens the protected targets without deleting one.
    ///
    /// Size. protect.finaleBossScale 0 makes the boss as tall as the World Events named bosses (bosses.json: Aurex, The
    /// Black Draught, Vhaleth...). Each named boss stands at its model's height (the client DAT's Setup) times its own
    /// ObjScale times world_events_boss_scale_multiplier; the boss gets the median of those, whatever its own model. A
    /// rat and an olthoi Warlord end up equally tall.
    ///
    /// Targets (protect.bossTargetsProtected). The boss goes for the healthiest standing target and the run names it ("Warlord
    /// X turns on Sir Donovan!"). Every bossSwapSeconds to bossSwapMaxSeconds it turns on a different one (the healthiest of
    /// the others). It goes for players only while a taunt holds it, while a damage pull holds it (players dealing
    /// bossPullShare of its health within bossPullWindowSeconds pull it onto the top damager for bossPullSeconds), or once no
    /// target stands. Its hits on a target are sized so that alone it needs bossMinKillSeconds to take one from full to 0,
    /// and its own bucket holds them to that. Its hits also count against the shared minDeathSeconds cap, so the boss and the wave together never empty a target faster than that.
    ///
    /// The rules here are pure and tested; WorldEvent.TickHoldFinaleBoss applies them on the world thread.
    /// </summary>
    public static class WorldEventFinaleBoss
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventFinaleBoss));

        /// <summary>The named bosses' median ObjScale (4.6) times world_events_boss_scale_multiplier (0.67): the fallback multiplier when no height can be read.</summary>
        public const double FallbackNamedScale = 3.08;

        /// <summary>The smallest and largest size multiplier the height match may give.</summary>
        public const float MinHeightScale = 0.5f, MaxHeightScale = 12f;

        // ---- size (pure) --------------------------------------------------------------------------

        /// <summary>The median of the heights above 0 (D6 - pure); null when there are none.</summary>
        public static double? MedianHeight(IEnumerable<double> heights)
        {
            var list = (heights ?? Enumerable.Empty<double>()).Where(h => double.IsFinite(h) && h > 0).OrderBy(h => h).ToList();

            if (list.Count == 0)
                return null;

            var mid = list.Count / 2;

            return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2;
        }

        /// <summary>
        /// The multiplier that makes a model <paramref name="ownHeight"/> tall at ObjScale <paramref name="ownObjScale"/>
        /// stand <paramref name="targetHeight"/> tall (D6 - pure), within <see cref="MinHeightScale"/> to
        /// <see cref="MaxHeightScale"/>. Null when any input is unusable.
        /// </summary>
        public static float? HeightScale(double targetHeight, double ownHeight, double ownObjScale)
        {
            if (!double.IsFinite(targetHeight) || targetHeight <= 0 || !double.IsFinite(ownHeight) || ownHeight <= 0)
                return null;

            var scale = double.IsFinite(ownObjScale) && ownObjScale > 0 ? ownObjScale : 1.0;

            var min = HoldSettings.Number(HoldKeys.FinaleHeightScaleMin, MinHeightScale);
            var max = Math.Max(min, HoldSettings.Number(HoldKeys.FinaleHeightScaleMax, MaxHeightScale));

            return (float)Math.Clamp(targetHeight / (ownHeight * scale), min, max);
        }

        /// <summary>A named boss's height in game (D6 - pure): its model's height times its ObjScale times the World Events boss multiplier.</summary>
        public static double RenderedHeight(double setupHeight, double objScale, double bossMultiplier) =>
            setupHeight * (double.IsFinite(objScale) && objScale > 0 ? objScale : 1.0) * (double.IsFinite(bossMultiplier) && bossMultiplier > 0 ? bossMultiplier : 1.0);

        private static readonly object sizeSync = new object();
        private static object sizeStore;
        private static double sizeMultiplier;
        private static double? namedHeight;
        private static int namedCount;

        /// <summary>
        /// The named bosses' median height in game (live: bosses.json's wcids, their weenies and the client DAT), cached per
        /// loaded store and boss multiplier. Null when none can be read.
        /// </summary>
        public static double? NamedBossHeight(double bossMultiplier, out int measured)
        {
            var store = WorldEventManager.Store;

            lock (sizeSync)
            {
                if (ReferenceEquals(store, sizeStore) && sizeMultiplier == bossMultiplier)
                {
                    measured = namedCount;
                    return namedHeight;
                }
            }

            var heights = new List<double>();

            try
            {
                foreach (var boss in store?.Bosses?.Values ?? Enumerable.Empty<Defs.BossDef>())
                {
                    if (boss == null || boss.NamedWcid == 0)
                        continue;

                    var weenie = DatabaseManager.World.GetCachedWeenie(boss.NamedWcid);
                    var setupId = weenie?.GetProperty(PropertyDataId.Setup) ?? 0;

                    if (setupId == 0 || DatManager.PortalDat == null)
                        continue;

                    var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(setupId);

                    if (setup == null || !(setup.Height > 0))
                        continue;

                    heights.Add(RenderedHeight(setup.Height, weenie.GetProperty(PropertyFloat.DefaultScale) ?? 1.0, bossMultiplier));
                }
            }
            catch (Exception ex)
            {
                log.Warn("[WORLDEVENT] could not measure the named bosses for the finale boss size", ex);
            }

            var median = MedianHeight(heights);

            lock (sizeSync)
            {
                sizeStore = store;
                sizeMultiplier = bossMultiplier;
                namedHeight = median;
                namedCount = heights.Count;
            }

            if (median != null)
                log.Info($"[WORLDEVENT] finale boss size: the {heights.Count} named bosses stand {heights.Min():0.0} to {heights.Max():0.0} m, median {median:0.0} m");

            measured = heights.Count;
            return median;
        }

        /// <summary>A model's height from the client DAT (live). Null when it cannot be read.</summary>
        public static double? SetupHeight(uint setupId)
        {
            try
            {
                var setup = setupId != 0 ? DatManager.PortalDat?.ReadFromDat<SetupModel>(setupId) : null;

                return setup != null && setup.Height > 0 ? setup.Height : null;
            }
            catch
            {
                return null;
            }
        }

        // ---- targets (pure) -------------------------------------------------------------------------

        /// <summary>
        /// The target the boss goes for (D6 - pure): with <paramref name="swap"/> false, <paramref name="current"/> while it
        /// stands, else the healthiest; with <paramref name="swap"/> true, the healthiest of the OTHERS (the current one only
        /// when it is the last standing). Health is the fraction current / max; ties keep the first. 0 when none stands.
        /// </summary>
        public static uint PickTarget(IReadOnlyList<(uint Guid, uint Current, uint Max)> targets, uint current, bool swap)
        {
            var standing = (targets ?? Array.Empty<(uint, uint, uint)>()).Where(t => t.Guid != 0 && t.Current > 0 && t.Max > 0).ToList();

            if (standing.Count == 0)
                return 0;

            if (!swap && current != 0 && standing.Any(t => t.Guid == current))
                return current;

            var pool = swap && standing.Count > 1 ? standing.Where(t => t.Guid != current).ToList() : standing;

            var best = pool[0];

            foreach (var t in pool)
                if ((double)t.Current / t.Max > (double)best.Current / best.Max)
                    best = t;

            return best.Guid;
        }

        /// <summary>The time to the next swap (D6 - pure): uniform between the two bounds, the lower one when they are equal or bad.</summary>
        public static double SwapDelay(Random rng, double minSeconds, double maxSeconds)
        {
            var lo = double.IsFinite(minSeconds) && minSeconds > 0 ? minSeconds : 15;
            var hi = double.IsFinite(maxSeconds) && maxSeconds > lo ? maxSeconds : lo;

            return lo + (rng ?? new Random()).NextDouble() * (hi - lo);
        }

        /// <summary>
        /// The player damage to the boss within the window ending at <paramref name="now"/> (D6 - pure): the total, and the
        /// player who dealt the most of it (0 when none).
        /// </summary>
        public static (double Total, uint Top) RecentDamage(IEnumerable<(double At, uint Player, double Amount)> log, double now, double windowSeconds)
        {
            var byPlayer = new Dictionary<uint, double>();
            var total = 0.0;

            foreach (var (at, player, amount) in log ?? Enumerable.Empty<(double, uint, double)>())
            {
                if (player == 0 || !(amount > 0) || !double.IsFinite(at) || now - at > windowSeconds || at > now)
                    continue;

                total += amount;
                byPlayer[player] = (byPlayer.TryGetValue(player, out var sum) ? sum : 0) + amount;
            }

            var top = byPlayer.Count == 0 ? 0u : byPlayer.OrderByDescending(kv => kv.Value).First().Key;

            return (total, top);
        }

        /// <summary>Whether that damage pulls the boss (D6 - pure): <paramref name="share"/> above 0 and at least that share of its max health.</summary>
        public static bool PullDue(double recentDamage, uint bossMaxHealth, double share) =>
            double.IsFinite(share) && share > 0 && bossMaxHealth > 0 && recentDamage >= share * bossMaxHealth;

        /// <summary>
        /// One boss hit on a protected target (D6 - pure): the slice of max health the time since its previous hit stands for,
        /// at <paramref name="minKillSeconds"/> for the whole, so the boss alone needs that long to empty a target.
        /// </summary>
        public static float HitDamage(uint maxHealth, double intervalSeconds, double minKillSeconds) =>
            WorldEventHoldRules.ProtectedHitDamage(maxHealth, intervalSeconds, minKillSeconds);

        /// <summary>The boss's own damage bucket (D6 - pure): at most max health per minKillSeconds, however its hits come.</summary>
        public static float CapHit(float requested, uint maxHealth, double minKillSeconds, ref double tokens, ref double lastAt, double now) =>
            WorldEventHoldRules.CapProtectedHit(requested, maxHealth, minKillSeconds, ref tokens, ref lastAt, now);

        /// <summary>"{boss} turns on {target}!" (D6 - pure), the default when there is no template.</summary>
        public static string TargetLine(string template, string boss, string target)
        {
            var line = string.IsNullOrWhiteSpace(template) ? "{boss} turns on {target}!" : template;

            return line.Replace("{boss}", string.IsNullOrWhiteSpace(boss) ? "The Warlord" : boss.Trim())
                       .Replace("{target}", string.IsNullOrWhiteSpace(target) ? "an envoy" : target.Trim());
        }

        /// <summary>"{boss} turns on {player}!" (D6 - pure), the default when there is no template.</summary>
        public static string PulledLine(string template, string boss, string player)
        {
            var line = string.IsNullOrWhiteSpace(template) ? "{boss} turns on {player}!" : template;

            return line.Replace("{boss}", string.IsNullOrWhiteSpace(boss) ? "The Warlord" : boss.Trim())
                       .Replace("{player}", string.IsNullOrWhiteSpace(player) ? "you" : player.Trim());
        }
    }
}
