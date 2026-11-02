using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Models;
using ACE.Server.Entity.Actions;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>
    /// World Events Hold H5: the finale boss's rules - which knight it goes for, when it swaps, when player damage pulls it,
    /// and how big it is (<see cref="DecideScale"/>, matched to the named bosses' height). Everything but
    /// <see cref="NamedBossHeight"/> and <see cref="SetupHeight"/> is pure. Ported from DreamWeave #17's WorldEventFinaleBoss
    /// (the per-hit damage cap is H4's and is not here).
    /// </summary>
    public static class WorldEventHoldFinaleBoss
    {
        /// <summary>The median of the heights above 0; null when there are none.</summary>
        public static double? MedianHeight(IEnumerable<double> heights)
        {
            var list = (heights ?? Enumerable.Empty<double>()).Where(h => double.IsFinite(h) && h > 0).OrderBy(h => h).ToList();

            if (list.Count == 0)
                return null;

            var mid = list.Count / 2;

            return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2;
        }

        /// <summary>
        /// The multiplier that makes a model <paramref name="ownHeight"/> tall at ObjScale <paramref name="ownObjScale"/> stand
        /// <paramref name="targetHeight"/> tall, within <paramref name="min"/> to <paramref name="max"/>. Null when an input is unusable.
        /// </summary>
        public static float? HeightScale(double targetHeight, double ownHeight, double ownObjScale, double min, double max)
        {
            if (!double.IsFinite(targetHeight) || targetHeight <= 0 || !double.IsFinite(ownHeight) || ownHeight <= 0)
                return null;

            var scale = double.IsFinite(ownObjScale) && ownObjScale > 0 ? ownObjScale : 1.0;
            var lo = double.IsFinite(min) && min > 0 ? min : 0.5;
            var hi = double.IsFinite(max) && max >= lo ? max : lo;

            return (float)Math.Clamp(targetHeight / (ownHeight * scale), lo, hi);
        }

        /// <summary>A named boss's height in game: its model's height times its ObjScale times the World Events boss multiplier.</summary>
        public static double RenderedHeight(double setupHeight, double objScale, double bossMultiplier) =>
            setupHeight * (double.IsFinite(objScale) && objScale > 0 ? objScale : 1.0) * (double.IsFinite(bossMultiplier) && bossMultiplier > 0 ? bossMultiplier : 1.0);

        /// <summary>
        /// The finale boss's size multiplier (pure; review finding F1). finale_scale above 0: that, within 1 to 2.5. 0 or
        /// less (the shipped defend_envoys): as tall as the named World Events bosses (<paramref name="namedHeight"/>, their
        /// median rendered height) given its own model height and ObjScale, within <paramref name="min"/> to
        /// <paramref name="max"/>; when either height cannot be read, <paramref name="bossMultiplier"/> - the multiplier every
        /// other World Events boss gets (WorldEventHoldRules.FinaleBossScale(0, ...)), never a fixed fallback constant.
        /// </summary>
        public static float DecideScale(double configured, double? namedHeight, double? ownHeight, double ownObjScale, float bossMultiplier,
            double min, double max, out string how)
        {
            if (double.IsFinite(configured) && configured > 0)
            {
                how = "finale_scale";
                return WorldEventHoldRules.FinaleBossScale(configured, bossMultiplier);
            }

            var match = namedHeight != null && ownHeight != null ? HeightScale(namedHeight.Value, ownHeight.Value, ownObjScale, min, max) : null;

            if (match != null)
            {
                how = $"to {namedHeight:0.0} m (named bosses' median; its model {ownHeight:0.00} m at ObjScale {ownObjScale:0.##})";
                return match.Value;
            }

            how = "the World Events boss multiplier (no height could be read)";
            return WorldEventHoldRules.FinaleBossScale(0, bossMultiplier);
        }

        private static readonly object sizeSync = new object();
        private static object sizeStore;
        private static float sizeMultiplier = float.NaN;
        private static double? namedHeight;

        /// <summary>
        /// The named bosses' median rendered height (live: bosses.json's named wcids, their cached weenies' Setup and
        /// DefaultScale, and the Setup's height from the client dat, read through the thread-safe DatDatabase.ReadFromDat),
        /// cached per loaded store and boss multiplier. Null when none can be read. Called on the world thread at spawn.
        /// </summary>
        public static double? NamedBossHeight(float bossMultiplier)
        {
            var store = WorldEventManager.Store;

            lock (sizeSync)
            {
                if (ReferenceEquals(store, sizeStore) && sizeMultiplier == bossMultiplier)
                    return namedHeight;
            }

            var heights = new List<double>();

            try
            {
                foreach (var boss in store?.Bosses?.Values ?? Enumerable.Empty<Defs.BossDef>())
                {
                    if (boss == null || boss.NamedWcid == 0)
                        continue;

                    var weenie = ACE.Database.DatabaseManager.World.GetCachedWeenie(boss.NamedWcid);
                    var setupId = weenie?.GetProperty(ACE.Entity.Enum.Properties.PropertyDataId.Setup) ?? 0;
                    var height = SetupHeight(setupId);

                    if (height != null)
                        heights.Add(RenderedHeight(height.Value, weenie.GetProperty(ACE.Entity.Enum.Properties.PropertyFloat.DefaultScale) ?? 1.0, bossMultiplier));
                }
            }
            catch (Exception ex)
            {
                log.Warn("[WORLDEVENT] could not measure the named bosses for the hold finale boss size", ex);
            }

            var median = MedianHeight(heights);

            // A miss (no dat yet, no weenie cached yet, a throw) is never cached: the next finale measures again.
            if (CachesNamedHeight(median))
            {
                lock (sizeSync)
                {
                    sizeStore = store;
                    sizeMultiplier = bossMultiplier;
                    namedHeight = median;
                }
            }

            return median;
        }

        /// <summary>Whether a measured median is kept (pure; review finding N3): only a real height, never null or 0.</summary>
        public static bool CachesNamedHeight(double? median) => median is double m && double.IsFinite(m) && m > 0;

        /// <summary>A model's height from the client dat (DatDatabase.ReadFromDat, thread safe). Null when it cannot be read.</summary>
        public static double? SetupHeight(uint setupId)
        {
            try
            {
                var setup = setupId != 0 ? ACE.DatLoader.DatManager.PortalDat?.ReadFromDat<ACE.DatLoader.FileTypes.SetupModel>(setupId) : null;

                return setup != null && setup.Height > 0 ? setup.Height : null;
            }
            catch
            {
                return null;
            }
        }

        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventHoldFinaleBoss));

        /// <summary>
        /// The knight the boss goes for: with <paramref name="swap"/> false, <paramref name="current"/> while it stands, else the
        /// healthiest; with <paramref name="swap"/> true, the healthiest of the OTHERS (the current one only when it is the last
        /// standing). Health is current / max; ties keep the first. 0 when none stands.
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

        /// <summary>The time to the next swap: uniform between the two bounds, the lower one when they are equal or bad (15 s when the lower is bad).</summary>
        public static double SwapDelay(Random rng, double minSeconds, double maxSeconds)
        {
            var lo = double.IsFinite(minSeconds) && minSeconds > 0 ? minSeconds : 15;
            var hi = double.IsFinite(maxSeconds) && maxSeconds > lo ? maxSeconds : lo;

            return lo + (rng ?? new Random()).NextDouble() * (hi - lo);
        }

        /// <summary>The player damage to the boss within the window ending at <paramref name="now"/>: the total, and the player who dealt the most (0 when none).</summary>
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

            var top = byPlayer.Count == 0 ? 0u : byPlayer.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;

            return (total, top);
        }

        /// <summary>Whether that damage pulls the boss: <paramref name="share"/> above 0 and at least that share of its max health.</summary>
        public static bool PullDue(double recentDamage, uint bossMaxHealth, double share) =>
            double.IsFinite(share) && share > 0 && bossMaxHealth > 0 && recentDamage >= share * bossMaxHealth;

        /// <summary>"{boss} turns on {target}!", the default when there is no template.</summary>
        public static string TargetLine(string template, string boss, string target)
        {
            var line = string.IsNullOrWhiteSpace(template) ? "{boss} turns on {target}!" : template;

            return line.Replace("{boss}", string.IsNullOrWhiteSpace(boss) ? "The Warlord" : boss.Trim())
                       .Replace("{target}", string.IsNullOrWhiteSpace(target) ? "an envoy" : target.Trim());
        }

        /// <summary>"{boss} turns on {player}!", the default when there is no template.</summary>
        public static string PulledLine(string template, string boss, string player)
        {
            var line = string.IsNullOrWhiteSpace(template) ? "{boss} turns on {player}!" : template;

            return line.Replace("{boss}", string.IsNullOrWhiteSpace(boss) ? "The Warlord" : boss.Trim())
                       .Replace("{player}", string.IsNullOrWhiteSpace(player) ? "you" : player.Trim());
        }
    }

    /// <summary>What the finale controller decided this tick.</summary>
    public enum HoldFinaleDecisionKind
    {
        /// <summary>Nothing changes (no boss, too soon, a pull or taunt still holds it).</summary>
        None,

        /// <summary>The boss goes for <see cref="HoldFinaleDecision.TargetGuid"/> (a knight).</summary>
        Target,

        /// <summary>Player damage pulled it onto <see cref="HoldFinaleDecision.PlayerGuid"/> until <see cref="HoldFinaleDecision.Until"/>.</summary>
        Pull,

        /// <summary>No knight stands: it fights the players.</summary>
        OnPlayers
    }

    /// <summary>One decision for the finale boss. Immutable.</summary>
    public readonly struct HoldFinaleDecision
    {
        public readonly HoldFinaleDecisionKind Kind;
        public readonly uint BossGuid;
        public readonly uint TargetGuid;
        public readonly uint PlayerGuid;
        public readonly double Until;

        /// <summary>For the log: "swap", "healthiest", "back from a pull", "its target fell", "pull", "no knight stands".</summary>
        public readonly string Reason;

        /// <summary>True when this decision differs from the last one sent (the caller announces it).</summary>
        public readonly bool Changed;

        public HoldFinaleDecision(HoldFinaleDecisionKind kind, uint bossGuid, uint targetGuid, uint playerGuid, double until, string reason, bool changed)
        {
            Kind = kind;
            BossGuid = bossGuid;
            TargetGuid = targetGuid;
            PlayerGuid = playerGuid;
            Until = until;
            Reason = reason;
            Changed = changed;
        }

        public static readonly HoldFinaleDecision None = new HoldFinaleDecision(HoldFinaleDecisionKind.None, 0, 0, 0, 0, null, false);

        public override string ToString() => $"{Kind} boss=0x{BossGuid:X8} target=0x{TargetGuid:X8} player=0x{PlayerGuid:X8} until={Until:0.#} ({Reason})";
    }

    /// <summary>
    /// World Events Hold H5: the finale boss controller. <see cref="Decide"/> is the pure step (world thread, once a second):
    /// keep the boss on one knight, swap to the healthiest other every swap interval, let enough player damage pull it, and let
    /// it go for the players once no knight stands. <see cref="Tick"/> is the world-thread driver: it reads the live boss and
    /// knights, decides, and ENQUEUES the decision on the boss's own landblock, where <see cref="Apply"/> (the sink) runs - so
    /// nothing here ever writes a creature from the world thread.
    ///
    /// OPEN (reported to the orchestrator): the boss's per-creature target and pull state belongs to H4
    /// (Creature_WorldEventHold). Until that lands, the default sink only logs; H6 wires <see cref="Apply"/> to H4's setters.
    ///
    /// THREADING: one controller per run; <see cref="Decide"/> and <see cref="Tick"/> are world thread only. The sink runs on
    /// the boss's landblock thread.
    /// </summary>
    public sealed class WorldEventHoldFinaleController
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventHoldFinaleController));

        private readonly Random rng;

        private uint bossGuid;
        private uint targetGuid;
        private uint pulledBy;
        private double nextSwapAt;
        private double nextTickAt;
        private bool onPlayers;

        public WorldEventHoldFinaleController(Random rng = null)
        {
            this.rng = rng ?? new Random();
        }

        /// <summary>The landblock-thread sink a decision is applied with (boss, decision). Default: log only.</summary>
        public Action<Creature, HoldFinaleDecision> Apply { get; set; } = (boss, d) =>
            log.Info($"[WORLDEVENT] hold finale boss {boss?.Name}: {d}");

        /// <summary>The knight the boss is on now (0 = none).</summary>
        public uint TargetGuid => targetGuid;

        /// <summary>
        /// The pure step. <paramref name="knights"/> are the standing knights' (guid, current, max) health;
        /// <paramref name="recentDamage"/> is the player damage to the boss inside the pull window and its top dealer;
        /// <paramref name="held"/> is true while a taunt or an earlier pull still holds it.
        /// </summary>
        public HoldFinaleDecision Decide(double now, uint boss, uint bossMaxHealth, IReadOnlyList<(uint Guid, uint Current, uint Max)> knights,
            (double Total, uint Top) recentDamage, bool held, WorldEventHoldTuning tuning)
        {
            tuning ??= WorldEventHoldTuning.Defaults;

            if (boss == 0)
            {
                if (bossGuid != 0)
                    Reset(0);

                return HoldFinaleDecision.None;
            }

            if (now < nextTickAt && boss == bossGuid)
                return HoldFinaleDecision.None;

            nextTickAt = now + 1.0;

            if (boss != bossGuid)
                Reset(boss);

            var standing = (knights ?? Array.Empty<(uint, uint, uint)>()).Where(k => k.Guid != 0 && k.Current > 0 && k.Max > 0).ToList();

            if (standing.Count == 0)
            {
                var changedToPlayers = !onPlayers;
                onPlayers = true;
                targetGuid = 0;

                return changedToPlayers
                    ? new HoldFinaleDecision(HoldFinaleDecisionKind.OnPlayers, boss, 0, 0, 0, "no knight stands", true)
                    : HoldFinaleDecision.None;
            }

            onPlayers = false;

            if (recentDamage.Top != 0 && WorldEventHoldFinaleBoss.PullDue(recentDamage.Total, bossMaxHealth, tuning.FinalePullShare))
            {
                var changed = pulledBy != recentDamage.Top;
                pulledBy = recentDamage.Top;

                return new HoldFinaleDecision(HoldFinaleDecisionKind.Pull, boss, 0, recentDamage.Top, now + Math.Max(0, tuning.FinalePullSeconds), "pull", changed);
            }

            if (held)
                return HoldFinaleDecision.None;

            var returning = pulledBy != 0;
            pulledBy = 0;

            var swap = targetGuid != 0 && (now >= nextSwapAt || returning);
            var pick = WorldEventHoldFinaleBoss.PickTarget(standing, targetGuid, swap);

            if (pick == 0)
                return HoldFinaleDecision.None;

            var isChange = pick != targetGuid;
            var reason = returning ? "back from a pull" : swap ? "swap" : targetGuid == 0 ? "healthiest" : isChange ? "its target fell" : "keep";

            if (isChange || swap || nextSwapAt == 0)
                nextSwapAt = now + WorldEventHoldFinaleBoss.SwapDelay(rng, tuning.FinaleSwapSeconds, tuning.FinaleSwapMaxSeconds);

            targetGuid = pick;

            return new HoldFinaleDecision(HoldFinaleDecisionKind.Target, boss, pick, 0, 0, reason, isChange || returning);
        }

        private void Reset(uint guid)
        {
            bossGuid = guid;
            targetGuid = 0;
            pulledBy = 0;
            nextSwapAt = 0;
            onPlayers = false;
        }

        /// <summary>
        /// World thread: decides for <paramref name="boss"/> (null = none standing) and enqueues the decision on the boss's own
        /// landblock, where <see cref="Apply"/> runs. Health is read for the decision only (the same read the spawner's status
        /// lines make); nothing on a creature is written here. <paramref name="recentDamage"/> and <paramref name="held"/> come
        /// from the caller (H4's per-creature damage log and pull/taunt state). Returns the decision, enqueued or not.
        /// </summary>
        public HoldFinaleDecision Tick(double now, Creature boss, IReadOnlyList<Creature> knights, (double Total, uint Top) recentDamage,
            bool held, WorldEventHoldTuning tuning)
        {
            var live = boss != null && !boss.IsDead && !boss.IsDestroyed ? boss : null;

            var health = (knights ?? Array.Empty<Creature>())
                .Where(k => k != null && !k.IsDead && !k.IsDestroyed)
                .Select(k => (k.Guid.Full, k.Health.Current, k.Health.MaxValue))
                .ToList();

            return TickCore(now, live?.Guid.Full ?? 0, live?.Health.MaxValue ?? 0, health, recentDamage, held, tuning, decision =>
            {
                var landblock = live?.CurrentLandblock;

                if (landblock == null)
                    return false;

                var sink = Apply;
                var bossRef = live;

                landblock.EnqueueAction(new ActionEventDelegate(() =>
                {
                    try
                    {
                        if (bossRef.IsDead || bossRef.IsDestroyed)
                            return;

                        sink?.Invoke(bossRef, decision);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[WORLDEVENT] hold finale boss {bossRef.Name}: applying {decision} threw", ex);
                    }
                }));

                return true;
            });
        }

        /// <summary>
        /// The testable core of <see cref="Tick"/>: decides, then hands a real decision to <paramref name="enqueue"/>. When it
        /// cannot be enqueued (the boss has no landblock this tick), the controller's state is rolled back to before the
        /// decision, so a transition (onto the players, a new target, a pull) is decided again next tick instead of being
        /// consumed without ever reaching the boss. Only the once-a-second gate is kept.
        /// </summary>
        public HoldFinaleDecision TickCore(double now, uint boss, uint bossMaxHealth, IReadOnlyList<(uint Guid, uint Current, uint Max)> knights,
            (double Total, uint Top) recentDamage, bool held, WorldEventHoldTuning tuning, Func<HoldFinaleDecision, bool> enqueue)
        {
            var saved = (bossGuid, targetGuid, pulledBy, nextSwapAt, onPlayers);

            var decision = Decide(now, boss, bossMaxHealth, knights, recentDamage, held, tuning);

            if (decision.Kind == HoldFinaleDecisionKind.None || boss == 0)
                return decision;

            if (enqueue != null && enqueue(decision))
                return decision;

            // A new boss stays adopted in its fresh (Reset) state, so the once-a-second gate holds for it too; an old boss
            // gets exactly the state it had before this decision.
            if (saved.bossGuid != boss)
                Reset(boss);
            else
                (bossGuid, targetGuid, pulledBy, nextSwapAt, onPlayers) = saved;

            return HoldFinaleDecision.None;
        }
    }
}
