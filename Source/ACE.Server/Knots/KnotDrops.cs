using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Knots
{
    /// <summary>What one run's Knot drop roll decided. Pure; see <see cref="KnotDrops.Roll"/>.</summary>
    public readonly struct KnotRollOutcome
    {
        public KnotRollOutcome(bool fiberDropped, int newMisses, int patternIndex)
        {
            FiberDropped = fiberDropped;
            NewMisses = newMisses;
            PatternIndex = patternIndex;
        }

        public bool FiberDropped { get; }

        /// <summary>The owner's KnotFiberMisses after the Fiber roll: 0 on a drop, one more on a dry roll.</summary>
        public int NewMisses { get; }

        /// <summary>Index into the enabled pinnacle list of the Pattern that dropped, or -1 for none.</summary>
        public int PatternIndex { get; }

        public bool PatternDropped => PatternIndex >= 0;
    }

    /// <summary>
    /// The Thread-side Knot drops (Docs/Knots/KNOTS-DESIGN.md sections 3.1 and 5), rolled at the run's Cleared
    /// announcement. Every SEAT rolls its own Fiber and its own independent Pattern, each into its own held pile: the
    /// run owner always (offline or not), plus every other roster member the daily survey files for
    /// (ThreadDungeonManager.SurveyRecipients: online and entered, not puzzle-removed). A solo run has exactly one seat,
    /// the owner. Per seat rolls were first rejected (design section 12) and reversed by the repo owner on 2026-10-21,
    /// because the per-player Fiber rate in a group was 1/N of solo. dynamic_dungeons_fiber_per_seat off restores the
    /// owner-only roll.
    ///
    /// Bad-luck protection lives on each rolling CHARACTER as PropertyInt.KnotFiberMisses, persisted across sessions:
    /// every dry Fiber roll adds the pity step to that character's next one, a drop resets it. Only the Fiber roll
    /// touches it.
    /// </summary>
    public static class KnotDrops
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Test seam: a uniform draw in [0, 1). Restore in a finally.</summary>
        internal static Func<double> Draw = () => Random.Shared.NextDouble();

        /// <summary>Test seam: a uniform index in [0, n). Restore in a finally.</summary>
        internal static Func<int, int> Pick = n => Random.Shared.Next(n);

        /// <summary>
        /// One Fiber roll with bad-luck protection (pure). Chance = min(1, base + misses x step), with a negative miss
        /// count read as 0 and NaN inputs as 0. A drop is <c>draw &lt; chance</c>, so a chance of 1 always drops for a
        /// draw in [0, 1) and a chance of 0 never does. Returns the drop and the new miss count (0 on a drop).
        /// </summary>
        public static (bool Dropped, int NewMisses) Decide(double baseChance, double step, int misses, double draw)
        {
            var m = Math.Max(0, misses);
            var b = double.IsNaN(baseChance) ? 0.0 : Math.Max(0.0, baseChance);
            var s = double.IsNaN(step) ? 0.0 : Math.Max(0.0, step);

            var chance = Math.Min(1.0, b + m * s);

            if (draw < chance)
                return (true, 0);

            return (false, m == int.MaxValue ? m : m + 1);
        }

        /// <summary>
        /// Whether a cleared run rolls Knot drops at all (pure). Never for a Thread-Guide run, a run with no stamped
        /// level (an empty plan, or a stamp that failed), a run on the legacy non-pooled loot path, a run that placed
        /// nothing (it cleared without a fight - the same gate the daily survey applies, ThreadDungeonManager's
        /// RecordSurvey), or with dynamic_dungeons_fiber_enabled off.
        /// </summary>
        internal static bool ShouldRoll(bool isGuide, int? knotLevel, bool pooledLoot, int spawned, bool fiberEnabled)
            => !isGuide && knotLevel.HasValue && knotLevel.Value > 0 && pooledLoot && spawned > 0 && fiberEnabled;

        /// <summary>
        /// The whole roll for one run (pure over its draws): the Fiber roll first, then an independent Pattern roll at
        /// <paramref name="patternChance"/>, skipped outright (no draw) when no pinnacle is enabled. The Pattern picks
        /// uniformly among the <paramref name="enabledPinnacles"/> through <paramref name="pick"/>.
        /// </summary>
        internal static KnotRollOutcome Roll(int misses, double fiberChance, double pityStep, double patternChance, int enabledPinnacles,
            Func<double> draw, Func<int, int> pick)
        {
            var (dropped, newMisses) = Decide(fiberChance, pityStep, misses, draw());

            var patternIndex = -1;

            if (enabledPinnacles > 0 && draw() < patternChance)
            {
                patternIndex = pick(enabledPinnacles);

                if (patternIndex < 0 || patternIndex >= enabledPinnacles)
                    patternIndex = -1;
            }

            return new KnotRollOutcome(dropped, newMisses, patternIndex);
        }

        /// <summary>
        /// Called exactly once per run, from ThreadDungeonManager.AnnounceCleared behind TryClaimClearedAnnouncement and
        /// before the survey. For each seat (the owner, plus every survey-eligible member when
        /// dynamic_dungeons_fiber_per_seat is on) rolls, creates whatever dropped, adds it to THAT seat's held pile (the
        /// pile solo and group delivery both drain), and writes that seat's miss counter. Never throws: a failure costs
        /// the drop, never the clear, and one seat's failure never costs another seat its roll.
        /// </summary>
        public static void OnRunCleared(ThreadDungeonRun run)
        {
            if (run == null)
                return;

            try
            {
                RollFor(run);
            }
            catch (Exception ex)
            {
                log.Error($"[KNOTS] {run} the Knot drop roll threw; no Knot drops for this run", ex);
            }
        }

        /// <summary>
        /// Test seam: opens one seat for rolling, returning its current KnotFiberMisses and the writer for the new
        /// count. Production resolves the online player, else the offline record. Restore in a finally.
        /// </summary>
        internal static Func<ThreadDungeonRun, uint, (int Misses, Action<int> Write)> OpenSeat = OpenSeatLive;

        /// <summary>Test seam: builds a Fiber of a level (null when the weenie did not create). Restore in a finally.</summary>
        internal static Func<int, WorldObject> FiberFactory = CreateFiber;

        /// <summary>Test seam: builds a Pattern for a pinnacle (null when the weenie did not create). Restore in a finally.</summary>
        internal static Func<PinnacleDef, WorldObject> PatternFactory = CreatePattern;

        private static void RollFor(ThreadDungeonRun run)
        {
            var level = run.KnotLevel;

            // The cheap run-state gates first, so a test run that never stamped a level reads no tunable at all.
            if (!ShouldRoll(ThreadGuideRules.IsGuide(run.Spec), level, run.PooledLoot, run.Spawned, true))
                return;

            if (!KnotTunables.FiberEnabled)
                return;

            // Available, not merely enabled: a Pattern for a pinnacle with no encounter would bind a Knot that can never enter.
            var enabled = KnotPinnacles.Available(ThreadDungeonManager.Store);

            RollSeats(run, level.Value, KnotTunables.FiberPerSeat, KnotTunables.FiberChance, KnotTunables.FiberPityStep,
                KnotTunables.PatternChance, enabled, guid => PlayerManager.GetOnlinePlayer(guid) != null);
        }

        /// <summary>
        /// Who rolls (pure over the roster): the owner always, in first position, then, when <paramref name="perSeat"/>,
        /// every member <see cref="ThreadDungeonManager.SurveyRecipients"/> names - the SAME predicate the daily survey
        /// files by, so the two can never drift. A solo run's only seat is its owner.
        /// </summary>
        internal static IReadOnlyList<uint> Seats(ThreadDungeonRun run, bool perSeat, Func<uint, bool> isOnline)
        {
            var seats = new List<uint> { run.OwnerGuid };

            // Solo clears (and the kill switch) never touch the roster snapshot or the survey predicate.
            if (!perSeat || !run.IsGroup)
                return seats;

            foreach (var guid in ThreadDungeonManager.SurveyRecipients(run.SnapshotRoster(), isOnline))
            {
                if (!seats.Contains(guid))
                    seats.Add(guid);
            }

            return seats;
        }

        /// <summary>Rolls every seat in order. One seat throwing is logged and costs only that seat.</summary>
        internal static void RollSeats(ThreadDungeonRun run, int level, bool perSeat, double fiberChance, double pityStep, double patternChance,
            IReadOnlyList<PinnacleDef> enabled, Func<uint, bool> isOnline)
        {
            foreach (var seat in Seats(run, perSeat, isOnline))
            {
                try
                {
                    RollSeat(run, seat, level, fiberChance, pityStep, patternChance, enabled);
                }
                catch (Exception ex)
                {
                    log.Error($"[KNOTS] {run} seat 0x{seat:X8} ({run.GetMember(seat)?.Name}) the Knot drop roll threw; no Knot drops for this seat", ex);
                }
            }
        }

        private static void RollSeat(ThreadDungeonRun run, uint seat, int level, double fiberChance, double pityStep, double patternChance,
            IReadOnlyList<PinnacleDef> enabled)
        {
            var (misses, write) = OpenSeat(run, seat);

            var outcome = Roll(misses, fiberChance, pityStep, patternChance, enabled.Count, Draw, Pick);

            var round = 0;
            int Round() => round != 0 ? round : (round = run.NextDealRound());

            var fiberPiled = false;
            uint? fiberGuid = null;

            if (outcome.FiberDropped)
            {
                var fiber = FiberFactory(level);

                if (fiber == null)
                    log.Error($"[KNOTS] {run} seat 0x{seat:X8} a Fiber dropped but wcid {KnotItems.FiberWcid} did not create; the miss counter is left at {misses}");
                else
                {
                    // read before Pile: a refused pile destroys the item
                    var guid = fiber.Guid.Full;
                    fiberPiled = Pile(run, seat, fiber, Round());

                    if (fiberPiled)
                        fiberGuid = guid;
                }
            }

            // The counter: a dry roll adds one, a delivered drop resets it. A drop that never reached the pile (the
            // weenie failed, or the run ended under us) leaves it untouched, so the seat keeps its pity.
            if (!outcome.FiberDropped || fiberPiled)
                write(outcome.NewMisses);

            string patternKey = null;
            uint? patternGuid = null;

            if (outcome.PatternDropped)
            {
                var def = enabled[outcome.PatternIndex];
                var pattern = PatternFactory(def);

                if (pattern == null)
                    log.Error($"[KNOTS] {run} seat 0x{seat:X8} a Pattern ({def.Key}) dropped but wcid {KnotItems.PatternWcid} did not create");
                else
                {
                    // read before Pile: a refused pile destroys the item
                    var guid = pattern.Guid.Full;

                    if (Pile(run, seat, pattern, Round()))
                    {
                        patternKey = def.Key;
                        patternGuid = guid;
                    }
                }
            }

            var missesNow = !outcome.FiberDropped || fiberPiled ? outcome.NewMisses : misses;

            log.Info($"[KNOTS] {run} seat 0x{seat:X8} ({run.GetMember(seat)?.Name}) rolled: level={level} misses={misses} fiber={(outcome.FiberDropped ? (fiberPiled ? "dropped" : "LOST") : "dry")} " +
                     $"missesNow={missesNow} pattern={patternKey ?? (outcome.PatternDropped ? "LOST" : "none")} " +
                     $"enabledPinnacles={enabled.Count}");

            KnotTelemetry.RecordRoll(run, seat, level, misses, missesNow, outcome.FiberDropped, fiberPiled, fiberGuid, enabled.Count, outcome.PatternDropped, patternKey, patternGuid);
        }

        private static (int Misses, Action<int> Write) OpenSeatLive(ThreadDungeonRun run, uint seat)
        {
            var online = PlayerManager.GetOnlinePlayer(seat);
            var offline = online == null ? PlayerManager.GetOfflinePlayer(seat) : null;

            var misses = online != null
                ? online.GetProperty(PropertyInt.KnotFiberMisses) ?? 0
                : offline?.GetProperty(PropertyInt.KnotFiberMisses) ?? 0;

            return (misses, value => WriteMisses(run, seat, online, offline, value));
        }
        private static WorldObject CreateFiber(int level)
        {
            var fiber = WorldObjectFactory.CreateNewWorldObject(KnotItems.FiberWcid);

            if (fiber == null)
                return null;

            fiber.SetProperty(PropertyInt.KnotLevel, level);
            fiber.SetProperty(PropertyString.Name, KnotItems.FiberName(level));

            return fiber;
        }

        private static WorldObject CreatePattern(PinnacleDef def)
        {
            var pattern = WorldObjectFactory.CreateNewWorldObject(KnotItems.PatternWcid);

            if (pattern == null)
                return null;

            pattern.SetProperty(PropertyString.PinnacleKey, def.Key);
            pattern.SetProperty(PropertyString.Name, KnotItems.PatternName(def.Name));

            return pattern;
        }

        /// <summary>Adds one Knot drop to a seat's held pile; destroys it and returns false when the pile refuses it.</summary>
        private static bool Pile(ThreadDungeonRun run, uint seat, WorldObject item, int round)
        {
            if (run.AddToHeldPile(seat, new HeldPileItem(item, null, round)))
                return true;

            log.Warn($"[KNOTS] {run} seat 0x{seat:X8}'s held pile refused {item.Name}; destroyed");
            item.Destroy();
            return false;
        }

        /// <summary>
        /// Writes a seat's miss counter: on the online player, else on the offline record and saved (the fork's
        /// offline-write idiom, Player_PvpRewards.GrantArenaBloodOffline). With neither reachable (mid-logout) the write
        /// is skipped and logged.
        /// </summary>
        private static void WriteMisses(ThreadDungeonRun run, uint seat, Player online, OfflinePlayer offline, int misses)
        {
            if (online != null)
            {
                online.SetProperty(PropertyInt.KnotFiberMisses, misses);
                return;
            }

            if (offline != null)
            {
                offline.SetProperty(PropertyInt.KnotFiberMisses, misses);
                offline.SaveBiotaToDatabase();
                return;
            }

            log.Warn($"[KNOTS] {run} seat 0x{seat:X8} is neither online nor offline (mid-logout?); KnotFiberMisses not written (would be {misses})");
        }

        // ---- delivery presentation ----

        /// <summary>
        /// The Fiber count the landed line shows (pure): the Fibers already in the character's inventory plus the ones
        /// that have just landed up to and including this one. KnotItems.FiberLanded caps the display at 10.
        /// </summary>
        internal static int FiberCountForLine(int heldInInventory, int landedSoFar) => Math.Max(0, heldInInventory) + Math.Max(1, landedSoFar);

        /// <summary>
        /// Called from ThreadCachePlacer.Deliver when Knot drops have just landed in <paramref name="cache"/>: the
        /// sound the rare branch plays, once, and the cache holder's chat line per drop. The Fiber count reads the holder's
        /// inventory (side packs and the Pouch included) on the holder's OWN action queue, never from this landblock
        /// thread. Fibers sitting in other uncollected caches are not counted (design section 3.4).
        /// </summary>
        public static void AnnounceLanded(ThreadDungeonRun run, Container cache, IReadOnlyList<WorldObject> landed)
        {
            if (cache == null || landed == null || landed.Count == 0)
                return;

            try
            {
                cache.ApplySoundEffects(Sound.TriggerActivated, 10);

                var ownerGuid = (cache as Chest)?.P_DungeonCacheOwnerGuid ?? run?.OwnerGuid ?? 0;
                var owner = PlayerManager.GetOnlinePlayer(ownerGuid);

                if (owner?.Session == null)
                    return;

                var fibers = landed.Count(KnotItems.IsFiber);
                var patternNames = landed.Where(KnotItems.IsPattern).Select(PatternDisplayName).ToList();

                owner.EnqueueAction(new ActionEventDelegate(() =>
                {
                    if (owner.Session == null)
                        return;

                    var held = owner.GetAllPossessions().Count(KnotItems.IsFiber);

                    for (var i = 1; i <= fibers; i++)
                        owner.Session.Network.EnqueueSend(new GameMessageSystemChat(KnotItems.FiberLanded(FiberCountForLine(held, i)), ChatMessageType.Broadcast));

                    foreach (var name in patternNames)
                        owner.Session.Network.EnqueueSend(new GameMessageSystemChat(KnotItems.PatternLanded(name), ChatMessageType.Broadcast));
                }));
            }
            catch (Exception ex)
            {
                log.Error($"[KNOTS] {run} announcing a landed Knot drop threw", ex);
            }
        }

        /// <summary>The pinnacle name a landed Pattern names: the registry's name for its key, else the key itself.</summary>
        private static string PatternDisplayName(WorldObject pattern)
        {
            var key = pattern.GetProperty(PropertyString.PinnacleKey);
            var def = ThreadDungeonManager.Store?.GetPinnacle(key);

            return def?.Name ?? key ?? "an unknown pinnacle";
        }
    }
}
