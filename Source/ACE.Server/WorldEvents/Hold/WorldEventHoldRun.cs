using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using log4net;

using ACE.Entity.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>
    /// One Hold run's shared state: the <see cref="IWorldEventHoldRunView"/> every Hold creature's tag points at, plus the
    /// world-thread writers that keep it current. Owned by HoldObjective.
    ///
    /// THREADING. There is exactly one writer thread: the world thread (HoldObjective's hooks, and the spawner's world-thread
    /// half). Every view member is readable from any thread:
    /// - single values (<see cref="IsActive"/>, <see cref="WaveNumber"/>, <see cref="ParticipantCount"/>, <see cref="Tuning"/>)
    ///   are volatile fields;
    /// - lists (<see cref="Knights"/>, <see cref="WaveMonsters"/>) are immutable arrays replaced whole with a volatile write,
    ///   never edited in place, so a reader holds a consistent array for as long as it likes;
    /// - the per-player healer record and the per-boss records sit behind one private lock, held only for dictionary work.
    /// The run never writes a creature field; a change a creature needs is enqueued on that creature's landblock by the
    /// slice that makes it.
    ///
    /// SETTINGS. <see cref="Stage"/> resolves every Hold row once and keeps that snapshot: its NextRun rows are what this run
    /// uses for its whole life. <see cref="Refresh"/> re-resolves no more than once a second and takes only the Live rows from
    /// the fresh resolve, so an admin's Live change reaches a running run within a second and a NextRun change never does.
    /// </summary>
    public sealed partial class WorldEventHoldRun : IWorldEventHoldRunView
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The fewest seconds between two Live re-resolves.</summary>
        public const double RefreshIntervalSeconds = 1.0;

        private readonly Func<GoalDef, WorldEventHoldSettings> resolve;

        private readonly object sync = new object();

        private volatile bool isActive;
        private volatile int waveNumber;
        private volatile int participantCount = 1;
        private volatile WorldEventHoldTuning tuning = WorldEventHoldTuning.Defaults;

        private Creature[] knights = Array.Empty<Creature>();
        private Creature[] waveMonsters = Array.Empty<Creature>();
        private uint[] placedKnightGuids = Array.Empty<uint>();

        // Written by the world thread only (once, at Stage). Volatile because the shrine and death lines read the goal's lines
        // from a landblock thread.
        private volatile GoalDef goal;

        // World thread only.
        private WorldEventHoldSettings staged;
        private double lastRefreshAt = double.NegativeInfinity;

        // Under sync.
        private readonly Dictionary<uint, double> healers = new Dictionary<uint, double>();
        private readonly Dictionary<uint, double> finaleBossSpawnedAt = new Dictionary<uint, double>();
        private readonly Dictionary<uint, (int Hits, double Dealt, double LastAt)> finaleBossHits = new Dictionary<uint, (int, double, double)>();
        private readonly Dictionary<uint, double> shrineReady = new Dictionary<uint, double>();
        private readonly Dictionary<uint, double> shrinePlayerReady = new Dictionary<uint, double>();

        private uint[] shrineGuids = Array.Empty<uint>();

        /// <param name="runId">The owning WorldEvent.RunId.</param>
        /// <param name="resolve">How the Hold settings are resolved for a goal; null is the live resolver (PropertyManager). A seam for tests.</param>
        public WorldEventHoldRun(uint runId, Func<GoalDef, WorldEventHoldSettings> resolve = null)
        {
            RunId = runId;
            this.resolve = resolve ?? (g => WorldEventHoldSettingsResolver.Resolve(g));
        }

        // ---- the view ---------------------------------------------------------------------------------------------

        /// <inheritdoc/>
        /// <remarks>Fixed at construction; any thread.</remarks>
        public uint RunId { get; }

        /// <inheritdoc/>
        /// <remarks>Volatile read; any thread.</remarks>
        public bool IsActive => isActive;

        /// <inheritdoc/>
        /// <remarks>Volatile read; any thread.</remarks>
        public int WaveNumber => waveNumber;

        /// <inheritdoc/>
        /// <remarks>Volatile read of an immutable snapshot; any thread. <see cref="WorldEventHoldTuning.Defaults"/> before <see cref="Stage"/>.</remarks>
        public WorldEventHoldTuning Tuning => tuning;

        /// <inheritdoc/>
        /// <remarks>Volatile read; any thread.</remarks>
        public int ParticipantCount => participantCount;

        /// <inheritdoc/>
        /// <remarks>Volatile read of an immutable array; any thread.</remarks>
        public IReadOnlyList<Creature> Knights => Volatile.Read(ref knights);

        /// <inheritdoc/>
        /// <remarks>Volatile read of an immutable array; any thread.</remarks>
        public IReadOnlyList<Creature> WaveMonsters => Volatile.Read(ref waveMonsters);

        /// <summary>Every knight guid this run has ever placed (the objective's "placed" count). Volatile read of an immutable array; any thread.</summary>
        public IReadOnlyList<uint> PlacedKnightGuids => Volatile.Read(ref placedKnightGuids);

        /// <summary>Guids of the placed knights not yet dead or destroyed, read from <see cref="Knights"/>. A new list each call; any thread.</summary>
        public IReadOnlyList<uint> LiveKnightGuids
        {
            get
            {
                var list = Volatile.Read(ref knights);
                var live = new List<uint>(list.Length);

                foreach (var k in list)
                    if (k != null && !k.IsDead && !k.IsDestroyed)
                        live.Add(k.Guid.Full);

                return live;
            }
        }

        /// <inheritdoc/>
        /// <remarks>Takes the run's lock; any thread. A null healer is ignored.</remarks>
        public void NoteKnightHealer(Player healer, double now)
        {
            if (healer == null)
                return;

            NoteKnightHealer(healer.Guid.Full, now);
        }

        /// <summary>The guid form of <see cref="NoteKnightHealer(Player, double)"/> (testable without a Player). Takes the run's lock; any thread. Guid 0 is ignored.</summary>
        public void NoteKnightHealer(uint healerGuid, double now)
        {
            if (healerGuid == 0 || !double.IsFinite(now))
                return;

            lock (sync)
                healers[healerGuid] = now;
        }

        /// <inheritdoc/>
        /// <remarks>Takes the run's lock and forgets anyone older than the memory window; any thread.</remarks>
        public IReadOnlyList<uint> RecentHealers(double now)
        {
            var memory = Math.Max(0, tuning.HealerMemorySeconds);

            lock (sync)
            {
                var stale = healers.Where(kv => now - kv.Value > memory).Select(kv => kv.Key).ToList();

                foreach (var key in stale)
                    healers.Remove(key);

                return healers.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
            }
        }

        /// <inheritdoc/>
        /// <remarks>Takes the run's lock; any thread. The first hit by each boss is logged at Info, the rest at Debug.</remarks>
        public void NoteFinaleBossHit(Creature boss, Creature knight, float dealt, double now)
        {
            var bossGuid = boss?.Guid.Full ?? 0;

            if (bossGuid == 0)
                return;

            bool first;
            (int Hits, double Dealt, double LastAt) record;

            lock (sync)
            {
                first = !finaleBossHits.TryGetValue(bossGuid, out record);
                record = (record.Hits + 1, record.Dealt + Math.Max(0f, dealt), now);
                finaleBossHits[bossGuid] = record;
            }

            var line = $"[WORLDEVENT] run={RunId} hold finale boss {boss.Name} hit {knight?.Name} for {dealt:0} (hits={record.Hits} total={record.Dealt:0})";

            if (first)
                log.Info(line);
            else
                log.Debug(line);
        }

        /// <summary>How many hits the finale boss <paramref name="bossGuid"/> has landed on knights. Takes the run's lock; any thread.</summary>
        public int FinaleBossHitCount(uint bossGuid)
        {
            lock (sync)
                return finaleBossHits.TryGetValue(bossGuid, out var r) ? r.Hits : 0;
        }

        /// <inheritdoc/>
        /// <remarks>Takes the run's lock; any thread. 1 for a boss this run never recorded spawning.</remarks>
        public double FinaleEnrageMult(Creature boss, double now) => FinaleEnrageMult(boss?.Guid.Full ?? 0, now);

        /// <summary>The guid form of <see cref="FinaleEnrageMult(Creature, double)"/>. Takes the run's lock; any thread.</summary>
        public double FinaleEnrageMult(uint bossGuid, double now)
        {
            double spawnedAt;

            lock (sync)
            {
                if (!finaleBossSpawnedAt.TryGetValue(bossGuid, out spawnedAt))
                    return 1.0;
            }

            var t = tuning;

            return EnrageMult(spawnedAt, now, t.FinaleEnrageSeconds, t.FinaleEnrageMult);
        }

        /// <summary>The enrage rule (pure): after <paramref name="seconds"/> alive (0 or less = never), <paramref name="mult"/> (never below 1); else 1.</summary>
        public static double EnrageMult(double spawnedAt, double now, double seconds, double mult)
        {
            if (!(seconds > 0) || !(spawnedAt > 0) || !double.IsFinite(now) || now - spawnedAt < seconds)
                return 1.0;

            return double.IsFinite(mult) && mult > 1.0 ? mult : 1.0;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Any thread (the using player's landblock, from GenericObject.ActOnUse). Only a shrine this run registered
        /// (<see cref="RegisterShrines"/>) is handled. While the run is over, or shrine is off, the shrine is quiet. A resting
        /// shrine, or a player still on shrine_player_cooldown_seconds, says how long is left. Otherwise the nearest standing
        /// knight within shrine_bind_range (or, with shrine_heals_all, every standing knight) is queued for a shrine_heal on
        /// its own landblock through WorldEventHoldHeals, the shrine rests for shrine_cooldown_seconds, and a use that
        /// restores nothing anywhere is given back with a line saying why (sent from the knight's landblock).
        /// </remarks>
        public bool TryUseShrine(WorldObject shrine, Player player, double now, out string reply)
        {
            reply = null;

            if (player == null)
                return false;

            return TryUseShrineCore(shrine, player.Guid.Full, player.Name,
                text => player.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast)), now, out reply);
        }

        /// <summary>
        /// <see cref="TryUseShrine"/> with the player reduced to its guid, its name and how to tell it something later (from the
        /// knight's landblock). Tests drive this directly: a Player cannot be built in the test harness.
        /// </summary>
        internal bool TryUseShrineCore(WorldObject shrine, uint playerGuid, string playerName, Action<string> tellLater, double now, out string reply)
        {
            reply = null;

            if (shrine == null || !IsShrine(shrine.Guid.Full))
                return false;

            var t = tuning;

            if (!isActive || !t.ShrineEnabled)
            {
                reply = WorldEventHoldRules.ShrineQuietLine;
                return true;
            }

            // H6: the shrine is the defenders' own. A player the presence ledger has never seen in the arena is refused,
            // and nothing is spent.
            if (!IsParticipant(playerGuid))
            {
                reply = WorldEventHoldRules.ShrineNotParticipantLine;
                return true;
            }

            var shrineGuid = shrine.Guid.Full;
            var playerCooldown = Math.Max(0, t.ShrinePlayerCooldownSeconds);

            double shrineReadyAt, playerReadyAt;

            lock (sync)
            {
                shrineReady.TryGetValue(shrineGuid, out shrineReadyAt);
                shrinePlayerReady.TryGetValue(playerGuid, out playerReadyAt);
            }

            switch (WorldEventHoldRules.ShrineUseCheck(now, shrineReadyAt, playerReadyAt, playerCooldown))
            {
                case HoldShrineUseState.ShrineResting:
                    reply = WorldEventHoldRules.ShrineCooldownLine(shrineReadyAt - now);
                    return true;

                case HoldShrineUseState.PlayerResting:
                    reply = WorldEventHoldRules.ShrinePlayerCooldownLine(playerReadyAt - now);
                    return true;
            }

            var nearest = NearestStandingKnight(shrine, t.ShrineBindRange);

            if (nearest == null)
            {
                reply = WorldEventHoldRules.ShrineNobodyLine;
                return true;
            }

            var cooldown = WorldEventHoldRules.ShrineCooldown(t.ShrineCooldownSeconds);

            lock (sync)
            {
                shrineReady[shrineGuid] = now + cooldown;

                if (playerCooldown > 0)
                    shrinePlayerReady[playerGuid] = now + playerCooldown;
            }

            var healed = new List<Creature>();

            if (t.ShrineHealsAll)
            {
                foreach (var k in Volatile.Read(ref knights))
                    if (k != null && !k.IsDead && !k.IsDestroyed)
                        healed.Add(k);
            }
            else
                healed.Add(nearest);

            // Queued heals plus one hold for the loop itself, so the give-back can never run before every queue attempt is
            // counted, whichever thread finishes last (a knight's landblock, or this one after a failed queue).
            var pending = healed.Count + 1;
            var anyHealed = false;
            var shareCount = 0;
            var lastOutcome = HoldHealOutcome.None;

            // One finished heal (or failed queue, or the loop's own hold): the last one with nothing useful restored anywhere
            // gives the use back (the shrine's rest and the player's own) and says why. A heal below the useful floor
            // (WorldEventHoldRules.ShrineHealCounts: min(2%, half of shrine_heal)) of
            // the knight's max health counts as nothing, so a shrine cannot be burned on a scratch.
            void Settle(bool useful, HoldHealOutcome outcome)
            {
                bool last, any;
                HoldHealOutcome why;

                lock (sync)
                {
                    anyHealed |= useful;

                    if (outcome != HoldHealOutcome.None)
                        lastOutcome = outcome;

                    last = --pending == 0;
                    any = anyHealed;
                    why = lastOutcome;
                }

                if (!last || any)
                    return;

                lock (sync)
                {
                    shrineReady.Remove(shrineGuid);
                    shrinePlayerReady.Remove(playerGuid);
                }

                if (Volatile.Read(ref shareCount) == 0)
                    return; // nothing was queued at all: the caller answers with the nobody line

                log.Info($"[WORLDEVENT] run={RunId} shrine use by {playerName}: {nearest.Name} restored nothing useful ({why}); the use is given back");

                tellLater?.Invoke(WorldEventHoldRules.ShrineWastedLine(nearest.Name, why));
            }

            foreach (var knight in healed)
            {
                var k = knight;

                var queued = WorldEventHoldHeals.Queue(k, t.ShrineHeal, HoldHealSource.Shrine, result =>
                {
                    var useful = WorldEventHoldRules.ShrineHealCounts(result.Healed, k.Health.MaxValue, t.ShrineHeal);

                    if (useful)
                    {
                        PlayEffect(k, t.ShrineUsedEffect);

                        // The contributor's shrine line (protect.lines.shrine), said by the knight beside the shrine. Already
                        // on that knight's landblock thread, so it is said at once.
                        if (ReferenceEquals(k, nearest))
                            SayNow(k, goal?.Protect?.Lines?.Shrine);

                        log.Info($"[WORLDEVENT] run={RunId} shrine used by {playerName}: {k.Name} +{result.Healed} health (now {k.Health.Current}/{k.Health.MaxValue}); ready again in {cooldown:0} s");
                    }

                    Settle(useful, result.Outcome);
                });

                if (queued)
                    Interlocked.Increment(ref shareCount);
                else
                    Settle(false, HoldHealOutcome.None);
            }

            Settle(false, HoldHealOutcome.None);

            if (Volatile.Read(ref shareCount) == 0)
            {
                // Not one knight could be reached (no landblock): nothing happened, so nothing is spent.
                lock (sync)
                {
                    shrineReady.Remove(shrineGuid);
                    shrinePlayerReady.Remove(playerGuid);
                }

                reply = WorldEventHoldRules.ShrineNobodyLine;
            }

            return true;
        }

        /// <summary>True when <paramref name="guid"/> is one of this run's registered shrines. Any thread.</summary>
        public bool IsShrine(uint guid)
        {
            foreach (var g in Volatile.Read(ref shrineGuids))
                if (g == guid)
                    return true;

            return false;
        }

        /// <summary>The nearest standing knight within <paramref name="range"/> metres of <paramref name="shrine"/>, or null.</summary>
        private Creature NearestStandingKnight(WorldObject shrine, double range)
        {
            var at = shrine.Location;

            if (at == null)
                return null;

            Creature best = null;
            var bestDist = double.MaxValue;

            foreach (var k in Volatile.Read(ref knights))
            {
                var loc = k?.Location;

                if (k == null || k.IsDead || k.IsDestroyed || loc == null)
                    continue;

                var d = at.DistanceTo(loc);

                if (d <= range && d < bestDist)
                {
                    best = k;
                    bestDist = d;
                }
            }

            return best;
        }

        /// <summary>Plays a named PlayScript on <paramref name="target"/> (none for null, "none" or an unknown name). Its own landblock thread.</summary>
        private static void PlayEffect(WorldObject target, string effect)
        {
            if (target?.CurrentLandblock == null || string.IsNullOrWhiteSpace(effect)
                || !Enum.TryParse<PlayScript>(effect.Trim(), true, out var script) || script == PlayScript.Invalid)
                return;

            target.EnqueueBroadcast(new GameMessageScript(target.Guid, script, 1.0f));
        }

        // ---- world-thread writers ---------------------------------------------------------------------------------

        /// <summary>
        /// World thread, once, at the run's OnStaged: resolves every Hold row for <paramref name="goal"/> and keeps that
        /// snapshot (its NextRun rows are frozen from here on), then publishes the first tuning. A throwing resolver leaves
        /// <see cref="WorldEventHoldTuning.Defaults"/> in place, is logged, and is retried by <see cref="Refresh"/>.
        /// </summary>
        public void Stage(GoalDef goal, int participants, double now)
        {
            this.goal = goal;
            participantCount = Math.Max(1, participants);
            stageAttempted = true;
            lastRefreshAt = now;

            try
            {
                StageResolve();
            }
            catch (Exception ex)
            {
                stageRetryLogged = true;
                log.Error($"[WORLDEVENT] run={RunId} hold settings resolve threw at stage; using the defaults until a retry succeeds", ex);
            }
        }

        /// <summary>
        /// World thread: the tuning a hook read before <see cref="Stage"/> sees (the run's Stage reads the arena radius, the
        /// idle window and the adjacent-landblock hold before the objective's OnStaged): the staged tuning once there is one,
        /// else one resolve of <paramref name="forGoal"/> for the run's participant count. A throwing resolver reads as
        /// <see cref="WorldEventHoldTuning.Defaults"/> (logged). Changes nothing.
        /// </summary>
        public WorldEventHoldTuning PreviewTuning(GoalDef forGoal)
        {
            if (staged != null)
                return tuning;

            try
            {
                var resolved = resolve(forGoal) ?? throw new InvalidOperationException("the Hold settings resolver returned null");

                return WorldEventHoldTuning.From(resolved, forGoal, participantCount);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} hold settings preview threw; using the defaults for the stage reads", ex);
                return WorldEventHoldTuning.Defaults;
            }
        }

        /// <summary>True once <see cref="Stage"/> has been called (whether or not its resolve succeeded). World thread only.</summary>
        private bool stageAttempted;

        /// <summary>True once a failed stage resolve has been logged, so a retry that keeps failing is not logged every second.</summary>
        private bool stageRetryLogged;

        /// <summary>The stage resolve: the snapshot whose NextRun rows this run keeps, and the first tuning from it.</summary>
        private void StageResolve()
        {
            var resolved = resolve(goal) ?? throw new InvalidOperationException("the Hold settings resolver returned null");

            tuning = WorldEventHoldTuning.From(resolved, goal, participantCount);
            staged = resolved;
        }

        /// <summary>
        /// World thread: at most once per <see cref="RefreshIntervalSeconds"/>, re-resolves the Live rows (keeping the
        /// staged NextRun rows) and swaps in a fresh tuning. A no-op before <see cref="Stage"/>. When the stage resolve
        /// failed, this retries THAT resolve instead (so the NextRun rows are frozen at the first success), rather than
        /// leaving the run on the unscaled defaults for good. Returns true when it rebuilt.
        /// </summary>
        public bool Refresh(double now)
        {
            if (!stageAttempted || now - lastRefreshAt < RefreshIntervalSeconds)
                return false;

            lastRefreshAt = now;

            if (staged == null)
            {
                try
                {
                    StageResolve();
                    log.Info($"[WORLDEVENT] run={RunId} hold settings resolved on retry; tuning is live");
                    return true;
                }
                catch (Exception ex)
                {
                    if (!stageRetryLogged)
                        log.Error($"[WORLDEVENT] run={RunId} hold settings resolve retry threw; still on the defaults", ex);

                    stageRetryLogged = true;
                    return false;
                }
            }

            try
            {
                var merged = MergeLive(staged, resolve(goal));
                tuning = WorldEventHoldTuning.From(merged, goal, participantCount);
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} hold settings refresh threw; keeping the previous tuning", ex);
                return false;
            }
        }

        /// <summary>
        /// The staged snapshot with every Live row taken from <paramref name="fresh"/> instead (pure). A row missing from
        /// <paramref name="fresh"/> keeps its staged value.
        /// </summary>
        public static WorldEventHoldSettings MergeLive(WorldEventHoldSettings stagedSettings, WorldEventHoldSettings fresh)
        {
            if (stagedSettings == null)
                throw new ArgumentNullException(nameof(stagedSettings));

            var freshKeys = fresh == null ? new HashSet<string>() : new HashSet<string>(fresh.Keys, StringComparer.Ordinal);
            var values = new Dictionary<string, WorldEventHoldSetting>(StringComparer.Ordinal);

            foreach (var key in stagedSettings.Keys)
            {
                var old = stagedSettings[key];

                values[key] = old.Row?.Applies == SettingApplies.Live && freshKeys.Contains(key) ? fresh[key] : old;
            }

            return new WorldEventHoldSettings(values);
        }

        /// <summary>World thread: the audience was re-sampled. Kept at least 1; the next <see cref="Refresh"/> re-sizes the tuning by it.</summary>
        public void SetParticipantCount(int participants) => participantCount = Math.Max(1, participants);

        /// <summary>World thread: the wave in progress (0 before the first).</summary>
        public void SetWaveNumber(int wave) => waveNumber = Math.Max(0, wave);

        /// <summary>World thread: the run went Active.</summary>
        public void MarkActive() => isActive = true;

        /// <summary>World thread: the run finished (any outcome). Hooks treat its creatures as untagged-equivalent from here.</summary>
        public void MarkInactive() => isActive = false;

        /// <summary>
        /// Publishes the knights (a copy; nulls dropped) as a new immutable array, and adds each to
        /// <see cref="PlacedKnightGuids"/>, which never shrinks. Meant for the world thread, but takes the run's lock for the
        /// read-modify-write of the placed set, so even concurrent publishes can never drop a guid. Readers stay lock-free.
        /// </summary>
        public void PublishKnights(IEnumerable<Creature> list)
        {
            var copy = (list ?? Enumerable.Empty<Creature>()).Where(c => c != null).ToArray();

            lock (sync)
            {
                var placed = Volatile.Read(ref placedKnightGuids);
                var union = placed.Concat(copy.Select(c => c.Guid.Full)).Distinct().ToArray();

                Volatile.Write(ref placedKnightGuids, union);
                Volatile.Write(ref knights, copy);
            }
        }

        /// <summary>Publishes the wave monsters (a copy; nulls dropped) as a new immutable array. Meant for the world thread; takes the run's lock so publishes are ordered.</summary>
        public void PublishWaveMonsters(IEnumerable<Creature> list)
        {
            var copy = (list ?? Enumerable.Empty<Creature>()).Where(c => c != null).ToArray();

            lock (sync)
                Volatile.Write(ref waveMonsters, copy);
        }

        /// <summary>
        /// The Shrines of Restoration this run placed (the spawner calls this once it has placed them). Replaces the set whole;
        /// meant for the world thread, readers stay lock-free. Nulls are dropped.
        /// </summary>
        public void RegisterShrines(IEnumerable<WorldObject> shrines)
        {
            var objects = (shrines ?? Enumerable.Empty<WorldObject>()).Where(s => s != null).GroupBy(s => s.Guid.Full).Select(g => g.First()).ToArray();
            var guids = objects.Select(s => s.Guid.Full).ToArray();

            lock (sync)
            {
                Volatile.Write(ref shrineGuids, guids);
                Volatile.Write(ref shrineObjects, objects);
            }
        }

        /// <summary>World thread: the finale boss <paramref name="bossGuid"/> was placed at <paramref name="at"/>; its enrage clock starts there. Takes the run's lock.</summary>
        public void NoteFinaleBossSpawned(uint bossGuid, double at)
        {
            if (bossGuid == 0 || !double.IsFinite(at))
                return;

            lock (sync)
                finaleBossSpawnedAt[bossGuid] = at;
        }
    }
}
