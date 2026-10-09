using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.MlTreasure;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// The registry of live digsite encounters, and the tick that drives them. Modelled on
    /// ThreadDungeonManager (a ConcurrentDictionary keyed by run id, a 1 s tick plus a 15 s reap, and a
    /// startLock serialising admission) with WorldEvent's run-object contract for the per-encounter state.
    ///
    /// WHAT MAKES A DIGSITE DIFFERENT FROM EVERY OTHER RUN-SHAPED SYSTEM IN THIS FORK, and what the design
    /// below is built around: an encounter runs on a SHARED, PERSISTENT, OUTDOOR landblock in the live
    /// world. Three consequences, each handled explicitly:
    ///
    ///   * SEVERAL CAN OVERLAP. Encounters are keyed by their own id, never by landblock, and admission
    ///     refuses a second encounter within ml_digsite_separation_metres of a live one so two fights do not
    ///     merge into one unreadable pile.
    ///   * UNINVOLVED PLAYERS ARE PRESENT. Nothing here teleports, gates or evicts anybody. A passer-by
    ///     inside the radius makes the fight bigger (the audience scan sizes it), but is paid only if they
    ///     actually dealt damage (MlDigsiteRewards: one chest per eligible player).
    ///   * THE LANDBLOCK CAN UNLOAD AND THE SERVER CAN RESTART UNDER A LIVE ENCOUNTER. On unload, every
    ///     creature is DESTROYED with no Die(), so no death hook fires and a hook-driven encounter would
    ///     hang forever holding a concurrency slot. The reap therefore POLLS for every end no hook can
    ///     report (MlDigsiteRules.ShouldEnd tests the landblock first). On restart the registry is gone with
    ///     the process, and nothing an encounter placed survives it - the persistence exclusion keeps every
    ///     creature and the chest out of the shard, and MlDigsiteOrphanFilter sweeps anything a hard kill
    ///     left behind the next time that landblock loads.
    ///
    /// THE END PIPELINE - one path, and nothing else calls <see cref="Finish"/>:
    ///
    ///     TryRequestEnd -> Tick (world thread) consumes it -> Finish -> MarkEnded -> Deliver -> TryClaimReward
    ///
    /// The death hook, the reap, the meter, a wave clock, an empty opening and /digsite bail all only
    /// REQUEST an end (MlDigsiteEncounter.TryRequestEnd; the first request wins under the encounter's lock).
    /// The tick then finishes it. Tick runs after LandblockManager.Tick has returned
    /// (WorldManager.UpdateGameWorld), and that call runs its physics, multi-threaded and single-threaded
    /// landblock passes to completion before returning (LandblockManager.Tick), so no landblock work is in
    /// flight while Finish reads live creatures' DamageHistory or destroys them. A request is consumed whatever state the landblock is in: an encounter whose
    /// landblock unloaded is finished like any other, and the chest step skips an unloaded anchor itself.
    ///
    /// THREADING. Tick() runs on the world thread from WorldManager.UpdateGameWorld.
    /// OnEncounterCreatureDied() runs on whichever landblock tick thread killed the creature, and only
    /// records and requests. TryStart() runs on whatever thread completed the dig. So the registry is a
    /// ConcurrentDictionary, every mutable field of an encounter is guarded by that encounter's own lock, and
    /// the admission sequence is serialised under <see cref="startLock"/> so two simultaneous digs cannot
    /// both pass the same cap.
    /// </summary>
    public static class MlDigsiteManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly ConcurrentDictionary<uint, MlDigsiteEncounter> encounters =
            new ConcurrentDictionary<uint, MlDigsiteEncounter>();

        /// <summary>
        /// Two cadences, one pass. The 1 s tick drives wave pacing and the corruption meter, both of which a
        /// player watches in real time; the reap decision (TTL, landblock gone, wiped) is measured in
        /// minutes and keeps its own 15 s timer rather than being re-taken every second.
        /// </summary>
        private static DateTime nextTick = DateTime.MinValue;
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

        private static DateTime nextReap = DateTime.MinValue;
        private static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Serialises admission: the cap check, the separation check and the registry insert. Held across
        /// nothing else - never across a spawn, which reaches the landblock.
        /// </summary>
        private static readonly object startLock = new object();

        private static int nextEncounterId;

        public static IReadOnlyList<MlDigsiteEncounter> LiveEncounters
            => encounters.Values.Where(e => e.State != MlDigsiteState.Ended).ToList();

        /// <summary>
        /// The live encounter with this id, or null. An O(1) lookup for the one caller that arrives holding
        /// only a persisted stamp rather than an object reference: MlDigsiteInterruptObject's use hook, which
        /// reads PropertyInt.MlDigsiteEncounterId off the object a player clicked. Deliberately NOT a scan of
        /// <see cref="LiveEncounters"/>, which allocates a list on every call.
        /// </summary>
        internal static MlDigsiteEncounter FindLiveEncounter(uint encounterId)
            => encounters.TryGetValue(encounterId, out var encounter) && encounter.State != MlDigsiteState.Ended
                ? encounter
                : null;

        // ---- admission ---------------------------------------------------------------------------------

        /// <summary>
        /// Opens an encounter on a completed ordinary dig.
        ///
        /// CALLED AFTER THE DOUBLOON PAYOUT AND AFTER THE MAP HAS BEEN CONSUMED, which is what makes every
        /// refusal free: there is nothing left to roll back, so a refused encounter costs the player neither
        /// their map nor their coins. The encounter is strictly additive to the dig payout, never a
        /// replacement for it.
        ///
        /// Returns false with a reason on every refusal, having registered nothing and placed nothing.
        /// </summary>
        public static bool TryStart(Player digger, out string reason) => TryStart(digger, null, 0, out reason, MlTreasureZone.Unknown);

        /// <summary>
        /// The same admission and open sequence as <see cref="TryStart(Player, out string)"/>, with the
        /// encounter type and (for a Boss Rush) its mechanic set overridable for one call - the
        /// /testtreasuremap admin test path (TreasureMapHandler.FinishDig reads
        /// PropertyInt.TreasureMapForcedDigsiteType / TreasureMapForcedMechanicSet off the map before it is
        /// consumed). Admission (<see cref="MlDigsiteRules.IsAdmitted"/> - enabled, one-per-digger,
        /// separation, the server-wide cap) is NOT bypassed by either override: a forced type still has to
        /// clear the same gate an ordinary roll does.
        /// </summary>
        /// <param name="forcedType">
        /// Replaces <see cref="MlDigsiteRules.PickType"/>'s roll outright when set; null (every ordinary
        /// caller) rolls as before.
        /// </param>
        /// <param name="forcedMechanicSetId">
        /// Forwarded to <see cref="MlDigsiteBossMechanics.Roll"/> for a Boss Rush encounter only; 0 defers
        /// to the live ml_digsite_bossrush_set_force tunable exactly as before. Ignored for any other type.
        /// </param>
        /// <param name="zone">
        /// Zone isolation (owner-approved design): the Marae Lassel zone this dig belongs to, carried onto
        /// the opened encounter (<see cref="MlDigsiteEncounter.Zone"/>) so every creature it spawns draws
        /// from that zone's own roster band. TreasureMapHandler.FinishDig resolves this from the completed
        /// map's own stamped zone (or the dig site's, for a legacy map) before calling in. Unknown - every
        /// caller before this parameter existed - applies no filter, unchanged from before zone isolation.
        /// </param>
        public static bool TryStart(Player digger, MlDigsiteType? forcedType, long forcedMechanicSetId, out string reason,
            MlTreasureZone zone = MlTreasureZone.Unknown)
        {
            reason = null;

            if (digger?.Location == null)
            {
                reason = "no digger";
                return false;
            }

            var anchor = new Position(digger.Location);
            var now = DateTime.UtcNow;

            MlDigsiteEncounter encounter;

            // The whole admission sequence is one critical section: every check below is a read of
            // `encounters` followed by a write to it, and TryStart is not guaranteed to run on the world
            // thread, so two simultaneous digs could otherwise both observe "one below the cap" and both
            // register. Spawning happens after the lock is released.
            lock (startLock)
            {
                var live = LiveEncounters;

                var diggerRunning = live.Any(e => e.DiggerGuid == digger.Guid.Full);
                var tooClose = live.Any(e => WithinSeparation(e.Anchor, anchor));

                if (!MlDigsiteRules.IsAdmitted(MlDigsiteTunables.Enabled, diggerRunning, tooClose,
                        live.Count, MlDigsiteTunables.MaxConcurrent, out reason))
                {
                    log.Debug($"[ML_DIGSITE] refused an encounter for {digger.Name}: {reason}");
                    return false;
                }

                var type = forcedType ?? MlDigsiteRules.PickType(MlDigsiteTunables.WeightWaves, MlDigsiteTunables.WeightCorruption,
                    MlDigsiteTunables.WeightBossRush, ThreadSafeRandom.Next(0.0f, 1.0f));

                var id = (uint)Interlocked.Increment(ref nextEncounterId);

                encounter = new MlDigsiteEncounter(id, digger.Guid.Full, digger.Name, type, anchor, now, zone);

                if (!encounters.TryAdd(id, encounter))
                {
                    reason = "the encounter could not be registered";
                    return false;
                }
            }

            log.Info($"[ML_DIGSITE] started {encounter} at {anchor.ToLOCString()}");

            OpenEncounter(encounter, forcedMechanicSetId);

            return true;
        }

        /// <summary>
        /// Places an encounter's opening set. Outside the admission lock, because every one of these paths
        /// reaches the landblock.
        ///
        /// An opening that places NOTHING requests its own end at once rather than being left standing: an
        /// encounter with no creatures has no objective to kill and would sit until its TTL paying a failure
        /// tier for a fight that never existed. The next tick finishes it, like every other end.
        /// </summary>
        private static void OpenEncounter(MlDigsiteEncounter encounter, long forcedMechanicSetId = 0)
        {
            var rng = NewRandom();
            var placed = 0;

            switch (encounter.Type)
            {
                case MlDigsiteType.WavesAndMiniBoss:
                    placed = SpawnNextWave(encounter, rng, announceStart: false);
                    break;

                case MlDigsiteType.CorruptionMeter:
                    // Round 16 redesign: a field, plus the first Corrupted mob (the Priority role). The
                    // Corrupted mob is the objective; killing ml_digsite_corruption_kills_required of them,
                    // one at a time, wins. The field keeps growing on its own cadence (DriveCorruption).
                    placed = SpawnNextWave(encounter, rng, announceStart: false);

                    if (MlDigsiteSpawner.TrySpawn(encounter, MlDigsiteRole.Priority, rng) != null)
                        placed++;
                    else
                        // Code-review fix: the field alone can be enough for `placed` to stay above zero
                        // below, which would otherwise leave the encounter running with NO objective and NO
                        // win path - it would idle to the TTL. Marking the debt here means DriveCorruption's
                        // retry (MlDigsiteRules.ShouldRetryCorruptedSpawn) picks it up on the very next tick.
                        encounter.MarkCorruptedSpawnPending();

                    break;

                case MlDigsiteType.BossRush:
                    // The mechanic set is rolled BEFORE the boss is placed, because MlDigsiteSpawner reads the
                    // rolled set's add scaling for every Add-role spawn - and because the roll must be settled
                    // before the first driver tick can find the boss.
                    //
                    // ROLLED, NEVER KEYED TO WHICH BOSS CAME UP. A uniform draw over the set table satisfies
                    // both halves of the tester's requirement at once ("which boss spawns is random each time
                    // ... if the pool has more than 5 bosses, reuse the sets on different bosses") and keeps
                    // adding a seventh boss to one line in MlDigsiteRoster.BossEntries with no mechanic work.
                    var mechanicSet = MlDigsiteBossMechanics.Roll(encounter, rng, forcedMechanicSetId);

                    if (MlDigsiteSpawner.TrySpawn(encounter, MlDigsiteRole.Boss, rng) != null)
                        placed++;

                    if (mechanicSet != null && placed > 0)
                    {
                        // The post-hoc record a tester or an operator reads back from the log, in the same
                        // shape as MlDigsiteSpawner's own spawn line. The live equivalents are the status line
                        // (BuildStatusLine, so /digsite shows it) and ml_digsite_bossrush_set_force.
                        log.Info($"[ML_DIGSITE] {encounter} Boss Rush mechanic {mechanicSet.Value} addhp={mechanicSet.Value.AddHealth:0.##} addspeed={mechanicSet.Value.AddSpeed:0.##}");
                    }

                    break;
            }

            if (placed == 0)
            {
                log.Warn($"[ML_DIGSITE] {encounter} placed nothing at all; ending it rather than leaving an empty encounter standing");
                encounter.TryRequestEnd(MlDigsiteRules.EndReasons.Abandoned, MlDigsiteResult.Failed);
                return;
            }

            Announce(encounter, OpeningLine(encounter.Type), ChatMessageType.WorldBroadcast);
        }

        /// <summary>
        /// OWNER RULING (2026-09-24): "Treasure map events should not have world event announcements or
        /// progress updates." A digsite opening no longer reaches anyone outside the encounter: the two
        /// realm-wide, World-Event-tagged lines this used to send (round 17, a start cue plus a line naming
        /// the nearest town and the anchor's exact map coordinates via WorldEventTownIndex.Describe, both
        /// routed through the real WorldEvent system's chat plumbing) are retired outright, not replaced. This
        /// digsite system stays a separate, lighter-weight mechanic from WorldEvent (see the class remarks);
        /// it now borrows none of that plumbing at all. The one remaining opening line is
        /// <see cref="OpeningLine"/> below, sent through <see cref="Announce"/> and so already scoped to the
        /// audience radius like every other in-encounter line.
        /// </summary>
        private static string OpeningLine(MlDigsiteType type)
        {
            switch (type)
            {
                case MlDigsiteType.CorruptionMeter:
                    return "The dig breaks into something hollow, and a sick light wells up out of it. Whatever is channelling it must be put down.";

                case MlDigsiteType.BossRush:
                    return "The ground splits wide. Something that was buried here on purpose climbs out.";

                default:
                    return "The dig collapses inward, and the island comes to see what you have uncovered.";
            }
        }

        // ---- the tick ----------------------------------------------------------------------------------

        /// <summary>
        /// Drives every live encounter. Runs on the world thread; self-rate-limited, so calling it from the
        /// heartbeat every pass is cheap.
        /// </summary>
        public static void Tick()
        {
            var now = DateTime.UtcNow;

            if (now < nextTick)
                return;

            nextTick = now + TickInterval;

            var reap = now >= nextReap;

            if (reap)
                nextReap = now + ReapInterval;

            foreach (var encounter in encounters.Values.ToList())
            {
                try
                {
                    if (encounter.State == MlDigsiteState.Ended)
                    {
                        // Belt and braces: Finish already removes the encounter. This catches anything that
                        // reached Ended by another path.
                        encounters.TryRemove(encounter.EncounterId, out _);
                        continue;
                    }

                    // The reap only REQUESTS an end; nothing it decides is finished here.
                    if (reap)
                        ReapOne(encounter, now);

                    // Boss Rush pays by the health removed, so the boss is sampled on EVERY tick - including
                    // the one that finishes the encounter - rather than only while it is being driven.
                    SampleBossHealth(encounter);

                    // A pending request (from this reap, a death hook in the landblock tick that just ran, a
                    // bail) stops the encounter being driven any further: no new wave, no meter tick.
                    if (!encounter.EndRequested)
                        Drive(encounter, now);

                    // THE one consumer of end requests, and so the one caller of Finish. Reached whether or not
                    // the anchor landblock is still loaded - an unloaded encounter must still be finished.
                    if (encounter.TryGetEndRequest(out var reason, out var result))
                        Finish(encounter, result, reason);
                }
                catch (Exception ex)
                {
                    // One bad encounter must not stop the others being ticked, and must not escape into
                    // WorldManager's heartbeat.
                    log.Error($"[ML_DIGSITE] {encounter} tick threw", ex);
                }
            }
        }

        /// <summary>
        /// The reap pass for one encounter. REQUESTS an end when MlDigsiteRules.ShouldEnd says so; the caller
        /// (Tick) consumes the request in the same pass.
        ///
        /// This is where an encounter whose landblock unloaded is caught - the case no death hook can ever
        /// report, because unloading destroys creatures without running Die().
        /// </summary>
        private static void ReapOne(MlDigsiteEncounter encounter, DateTime now)
        {
            var anchor = encounter.Anchor;

            var landblockLoaded = anchor != null && LandblockManager.IsLoaded(anchor.LandblockId, anchor.Instance);

            // Probed only on a reap pass, never on the 1 s tick: this walks the online player list.
            var present = landblockLoaded && MlDigsiteAudience.AnyPresent(anchor, MlDigsiteTunables.AudienceRadiusMetres);

            if (present)
            {
                encounter.MarkPresence(now);

                // Per-player presence for group rewards: a helper is paid only if seen alive here within
                // ml_digsite_presence_window_seconds. Stamped every reap, so a wipe (nobody alive at the end)
                // still pays the group that was fighting a few seconds earlier.
                encounter.MarkPlayersPresent(PresentPlayerGuids(anchor), now);

                // RoZ round 19: a player who arrives mid-fight re-sizes what is already standing.
                RecheckStandingObjectives(encounter);
            }

            if (!MlDigsiteRules.ShouldEnd(encounter.State, landblockLoaded, present, now - encounter.StartedUtc,
                    now - encounter.LastPresenceUtc, MlDigsiteTunables.Ttl, MlDigsiteTunables.WipeGrace, out var why))
            {
                return;
            }

            // Round 16: every shape now pays by how far it got, times the fail multiplier
            // (MlDigsiteRules.EncounterPayoutFraction) - a WIN never reaches ReapOne at all, since the win
            // itself already requested the end. Failed vs Scored is kept only so the log line and a Corruption
            // loss's wording can differ from a Waves/Boss Rush non-win; the payout no longer reads this label.
            var result = encounter.Type == MlDigsiteType.CorruptionMeter ? MlDigsiteResult.Failed : MlDigsiteResult.Scored;

            encounter.TryRequestEnd(why, result);
        }

        /// <summary>
        /// RoZ round 19 level-spread scaling, the standing re-check on every reap pass: an objective creature
        /// already on the field (the Boss Rush boss, a Corruption encounter's Corrupted mob, a Waves checkpoint
        /// mini-boss) was sized for whoever was present when it spawned. When the audience changes -
        ///
        ///   * the Boss Rush boss's health multiplier is RAISED on the power-sum curve (WorldEvent.RatchetMult -
        ///     never lowered, so a thinning crowd never shrinks a bar players are already chewing through), and
        ///   * every standing objective's defenses are LOWERED (never raised) for a weaker player who arrived,
        ///     through the same MlMapEventScaling.LowerDefenses the spawn path uses.
        ///
        /// The writes are enqueued on the creature's own landblock, with the try/catch INSIDE the queued action
        /// (EnqueueAction escapes the caller's try/catch). Aun Relaria has no owner tick and is not re-checked.
        /// </summary>
        private static void RecheckStandingObjectives(MlDigsiteEncounter encounter)
        {
            var settings = MlMapEventTunables.Read();

            if (!settings.Enabled)
                return;

            var objective = encounter.ObjectiveCreature;
            var checkpoint = encounter.CheckpointCreature;

            if (objective == null && checkpoint == null)
                return;

            var profiles = MlDigsiteAudience.Sample(encounter.Anchor, MlDigsiteTunables.AudienceRadiusMetres);

            if (profiles.Count == 0)
                return;

            var weakest = MlMapEventScaling.Weakest(profiles);

            var raiseTo = 0.0;

            if (encounter.Type == MlDigsiteType.BossRush && objective != null
                && encounter.TryGetBossHealthBase(out var baseSv, out var baseMax, out _))
            {
                var powerSum = MlMapEventScaling.PowerSum(profiles);
                var candidate = MlMapEventScaling.BossHealthMult(settings.BossPowerPerPlayer, settings.BossHealthCap, powerSum);

                if (encounter.RaiseBossHealthMult(candidate))
                {
                    encounter.TryGetBossHealthBase(out _, out _, out raiseTo);

                    log.Info($"[ML_DIGSITE] spread recheck {encounter} boss health mult -> {raiseTo:F2} (powerSum={powerSum:F3}, players={profiles.Count})");

                    EnqueueStandingRecheck(encounter, objective, weakest, settings, baseSv, baseMax, raiseTo);
                    objective = null;
                }
            }

            if (objective != null)
                EnqueueStandingRecheck(encounter, objective, weakest, settings, 0, 0, 0);

            if (checkpoint != null)
                EnqueueStandingRecheck(encounter, checkpoint, weakest, settings, 0, 0, 0);
        }

        private static void EnqueueStandingRecheck(MlDigsiteEncounter encounter, Creature target, MlMapEventProfile weakest,
            MlMapEventSettings settings, uint bossBaseSv, uint bossBaseMax, double bossMult)
        {
            if (target.IsDestroyed || target.IsDead)
                return;

            var landblock = target.CurrentLandblock;

            if (landblock == null)
                return;

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (target.IsDestroyed || target.IsDead || !encounter.RunValid)
                        return;

                    var summary = ApplyStandingRecheck(target, weakest, settings, bossBaseSv, bossBaseMax, bossMult);

                    if (summary.Length > 0)
                        log.Info($"[ML_DIGSITE] spread recheck {encounter} wcid={target.WeenieClassId} key={weakest}{summary}");
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {encounter} spread recheck threw for 0x{target.Guid.Full:X8}", ex);
                }
            }));
        }

        /// <summary>
        /// The body of one standing re-check, run INSIDE the landblock action EnqueueStandingRecheck queues (and
        /// internal so the tests drive it directly). Two writes, both one-directional:
        ///
        ///   * boss health, only when <paramref name="bossMult"/> is above 1 and the scaled StartingValue is
        ///     STRICTLY above the current one - equal or lower is nothing to do, and lowering is never allowed.
        ///     The max-health gain is added to Current too, so the bar does not appear to drop.
        ///   * defenses, through MlMapEventScaling.LowerDefenses, which only ever lowers.
        ///
        /// An objective or checkpoint is passed bossMult 0, so its health is never touched here.
        /// Returns the log summary ("" when nothing changed).
        /// </summary>
        internal static string ApplyStandingRecheck(Creature target, MlMapEventProfile weakest, MlMapEventSettings settings,
            uint bossBaseSv, uint bossBaseMax, double bossMult)
        {
            var summary = "";

            if (bossMult > 1.0)
            {
                var scaled = WorldEventSpawner.ScaledStartingValue(bossBaseSv, bossBaseMax, bossMult);

                if (scaled > target.Health.StartingValue)
                {
                    var before = target.Health.MaxValue;

                    target.Health.StartingValue = scaled;

                    var after = target.Health.MaxValue;

                    if (after > before)
                        target.UpdateVitalDelta(target.Health, (int)Math.Min(int.MaxValue, after - before));

                    summary += $" hp={before}->{after}";
                }
            }

            var clamped = new List<string>();

            summary += MlMapEventScaling.LowerDefenses(target, weakest, settings.MinHitChance, settings.MinSpellLandChance, clamped);

            if (clamped.Count > 0)
                summary += " clamped=" + string.Join(",", clamped);

            return summary;
        }
        /// <summary>Runs one encounter's own type-specific pacing for this tick.</summary>
        private static void Drive(MlDigsiteEncounter encounter, DateTime now)
        {
            switch (encounter.Type)
            {
                case MlDigsiteType.WavesAndMiniBoss:
                    DriveWaves(encounter, now);
                    break;

                case MlDigsiteType.CorruptionMeter:
                    DriveCorruption(encounter, now);
                    break;

                case MlDigsiteType.BossRush:
                    // The mechanic driver (round 13 feedback, "Boss mechanics test"). Everything else about a
                    // Boss Rush is still unpaced: the boss dies (a win, requested by the death hook), or the
                    // TTL / wipe reap or a bail ends it and it pays by the health removed. With
                    // ml_digsite_bossrush_mechanics_enabled off, or with no set attached, this is one field
                    // read and the branch is the no-op it was before.
                    MlDigsiteBossMechanics.Drive(encounter, now);

                    // RoZ round 19: the measured boss-damage controller (World Event rule, sized to the
                    // toughest present player). One field read until a full sample window is buffered.
                    MlDigsiteBossDamage.Drive(encounter);
                    break;
            }

            // Tracker (round 13 feedback item E, part 3): boss HP milestones and the periodic status line
            // run for every type on the same tick, each self-gated on whether there is anything to report.
            DriveBossHealthMilestones(encounter);
            DrivePeriodicStatus(encounter, now);
            DriveTetherHeal(encounter);
        }

        /// <summary>
        /// The leash heal (round 13 feedback, "Open-area safeguard": "leash each boss so that if it is pulled
        /// too far from its spawn it returns and heals"). The existing tether already returns it - the heal
        /// half genuinely did not exist anywhere in this codebase: CheckMissHome -> MoveToHome -> ForceHome ->
        /// Sleep touches no Health field at any point.
        ///
        /// HOOKED IN THE DIGSITE TICK, NOT IN NAVIGATION, and that choice is the whole of the risk control.
        /// Monster.Sleep() is the tempting hook and is the wrong one: it is virtual, it is called on EVERY
        /// monster in the world on every de-aggro, and healing there would make every monster on the server
        /// un-attritionable - walk away, it heals. MoveToHome has the same blast radius. This sees only the
        /// creature the encounter placed, touches no shared navigation code, and costs one field read per
        /// second per Boss Rush encounter.
        ///
        /// SCOPED TO THE BOSS RUSH BOSS by owner ruling (2026-09-20): the tester asked about bosses, and it
        /// keeps the change off a path every digsite creature walks.
        ///
        /// MonsterState is the signal. MoveToHome is the only place it is ever set to State.Return (that
        /// method's own comment says so), so "walking home" is exactly what the latch fires on. The write is
        /// ENQUEUED on the creature's own landblock rather than performed inline: SampleBossHealth READS a
        /// vital from the world thread and argues that is safe, but a WRITE is a stronger claim than a read,
        /// so it goes through the queue rather than inheriting that argument.
        ///
        /// The heal is never silent - a boss that heals with no line reads as a bug to a player who merely
        /// died and is corpse-running back.
        /// </summary>
        private static void DriveTetherHeal(MlDigsiteEncounter encounter)
        {
            if (encounter.Type != MlDigsiteType.BossRush || !MlDigsiteTunables.TetherHealEnabled)
                return;

            var boss = encounter.ObjectiveCreature;

            if (boss == null || boss.IsDestroyed || boss.IsDead)
                return;

            var returning = boss.MonsterState == Creature.State.Return;

            var landblock = boss.CurrentLandblock;

            // A boss mid-adjacency-transfer has no landblock to queue onto. Returning WITHOUT claiming the
            // latch means the next tick tries again, rather than burning the one heal this return trip gets.
            if (returning && landblock == null)
                return;

            if (!encounter.TryClaimTetherHeal(returning))
                return;

            var target = boss;

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (target.IsDestroyed || target.IsDead || !encounter.RunValid)
                        return;

                    if (target.Health.Current >= target.Health.MaxValue)
                        return;

                    target.Health.Current = target.Health.MaxValue;

                    // The push that refreshes a watching player's health bar; without it the client keeps
                    // showing the bar it last saw and the heal looks like it did not happen.
                    target.OnHealthUpdate();
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {encounter} tether heal threw for 0x{target.Guid.Full:X8}", ex);
                }
            }));

            log.Info($"[ML_DIGSITE] {encounter} boss 0x{boss.Guid.Full:X8} broke tether and is returning; healing to full");

            Announce(encounter, $"{boss.Name} breaks off and walks back to the pit. Its wounds close as it goes.");
        }

        /// <summary>
        /// Folds the Boss Rush boss's current health into the encounter's running minimum
        /// (MlDigsiteEncounter.NoteBossHealthSample), which is what a Boss Rush that does not kill its boss is
        /// paid by. Read on the world thread after the landblock tick has joined, so the vital read cannot race
        /// a hit. A dead or missing boss is not sampled: a kill pays in full on its own, and a boss destroyed
        /// by an unload keeps the last minimum it was seen at.
        /// </summary>
        private static void SampleBossHealth(MlDigsiteEncounter encounter)
        {
            if (encounter.Type != MlDigsiteType.BossRush)
                return;

            var creature = encounter.ObjectiveCreature;

            if (creature == null || creature.IsDestroyed || creature.IsDead)
                return;

            var max = creature.Health.MaxValue;

            if (max == 0)
                return;

            encounter.NoteBossHealthSample((double)creature.Health.Current / max);
        }

        /// <summary>
        /// Boss HP milestone announcements (75/50/25%): for each live checkpoint mini-boss of a Waves encounter
        /// (the latch resets per checkpoint, MlDigsiteEncounter.TrackCheckpoint), and throughout a Boss Rush
        /// encounter. A Corruption encounter's Corrupted mob is not a "boss" in this sense - its progress is
        /// the kill count (OnCorruptedDied's "Corrupted slain: N of M" line), not an HP-percent milestone.
        /// </summary>
        private static void DriveBossHealthMilestones(MlDigsiteEncounter encounter)
        {
            if (encounter.Type == MlDigsiteType.CorruptionMeter)
                return;

            var creature = encounter.Type == MlDigsiteType.WavesAndMiniBoss
                ? encounter.CheckpointCreature
                : encounter.ObjectiveCreature;

            if (creature == null || creature.IsDestroyed || creature.IsDead)
                return;

            var max = creature.Health.MaxValue;
            var current = creature.Health.Current;

            foreach (var pct in encounter.DueBossHealthMilestones(current, max))
            {
                // Round 13 owner ruling (2026-09-20): when an immune phase fires at the same threshold, this
                // line is SUPPRESSED. Two lines about the same moment - "The boss is at 50% health!" followed
                // by "It hardens over" - reads as a bug, and the immune line is the one that tells a player
                // what to do about it. The milestone is still LATCHED (DueBossHealthMilestones consumed it),
                // so it is suppressed once and does not resurface later in the fight.
                if (ImmunePhaseOwnsMilestone(encounter, pct))
                    continue;

                Announce(encounter, $"The boss is at {pct}% health!");
            }
        }

        /// <summary>
        /// Whether this encounter's rolled mechanic set turns the boss immune at the same percent a milestone
        /// line would announce. False for every encounter that is not a Boss Rush, and for a Boss Rush whose
        /// set carries no immune mechanic - which is the only path the Waves and Corruption shapes can take.
        /// </summary>
        private static bool ImmunePhaseOwnsMilestone(MlDigsiteEncounter encounter, int milestonePercent)
        {
            var state = encounter.BossMechanics;

            if (state == null)
                return false;

            return MlDigsiteBossMechanicRules.ImmuneCoversMilestone(state.Set, MlDigsiteTunables.BossRushImmuneArgs, milestonePercent);
        }

        /// <summary>
        /// The periodic status line (default every 30 s, ml_digsite_status_interval_seconds): the same
        /// compact line <see cref="BuildStatusLine"/> composes for /digsite, sent unprompted to every
        /// participant so a fight in progress does not require asking.
        /// </summary>
        private static void DrivePeriodicStatus(MlDigsiteEncounter encounter, DateTime now)
        {
            if (!encounter.TryClaimStatusTick(now, MlDigsiteTunables.StatusInterval))
                return;

            Announce(encounter, BuildStatusLine(encounter, now));
        }

        /// <summary>
        /// Composes one encounter's compact status line - shared by <see cref="DrivePeriodicStatus"/> and the
        /// /digsite command (MlDigsiteCommands.cs), so a player who asks sees exactly what the periodic
        /// broadcast would have told them.
        /// </summary>
        public static string BuildStatusLine(MlDigsiteEncounter encounter, DateTime now)
        {
            return BuildStatusLine(encounter, now, MlDigsiteTunables.Ttl, MlDigsiteTunables.WaveTimeLimit,
                MlDigsiteTunables.StallTimeout, MlDigsiteTunables.CorruptionKillsRequired, CurrentTierPercent(encounter));
        }

        /// <summary>
        /// The testable half of <see cref="BuildStatusLine(MlDigsiteEncounter, DateTime)"/>: every tunable it
        /// would otherwise read live, PLUS the already-resolved payout tier percent, taken as parameters
        /// instead. <see cref="CurrentTierPercent"/> alone reads five more keys through
        /// <see cref="MlDigsiteTunables.Payout"/> (the wave tier table, checkpoint bonus, Boss Rush floor,
        /// partial fraction, fail multiplier) - pulling every one of those into a unit test just to exercise
        /// the WavesAndMiniBoss gate, the TTL-vs-wave-clock comparison and the waveLeft wiring below would be
        /// testing the payout system's own plumbing by accident, so <paramref name="tierPct"/> is resolved by
        /// the caller instead. PropertyManager reads throw under the test harness for an uncached key, which
        /// is exactly what makes this split worth having: the public overload above is the only caller in
        /// ACE.Server, and this one lets a test drive every branch below with plain parameters.
        ///
        /// internal rather than private so ACE.Server.Tests (InternalsVisibleTo) can call it directly.
        /// </summary>
        internal static string BuildStatusLine(MlDigsiteEncounter encounter, DateTime now, TimeSpan ttl,
            TimeSpan waveTimeLimit, TimeSpan stallTimeout, int corruptionKillsRequired, int tierPct)
        {
            var timeLeft = ttl - (now - encounter.StartedUtc);

            var bossHpPct = 0;

            if (encounter.Type == MlDigsiteType.BossRush)
            {
                var creature = encounter.ObjectiveCreature;

                if (creature != null && !creature.IsDestroyed && !creature.IsDead && creature.Health.MaxValue > 0)
                    bossHpPct = (int)Math.Clamp(creature.Health.Current * 100L / creature.Health.MaxValue, 0, 100);
            }

            // The live half of "which set am I fighting", alongside the log line at the roll and
            // ml_digsite_bossrush_set_force. A tester driving a client cannot read a log, and a seed is not
            // reachable from in game; the status line is.
            var mechanics = encounter.BossMechanics;

            var extra = mechanics == null
                ? null
                : $"Mechanics: set {mechanics.Set.SetId} ({MlDigsiteRules.MechanicName(mechanics.Set.Main)}"
                    + (mechanics.Set.Secondary == MlDigsiteMechanic.None
                        ? ")."
                        : $" + {MlDigsiteRules.MechanicName(mechanics.Set.Secondary)}).");

            int? waveLeft = null;

            // Round 17 tester feedback: "killed everything on radar but the wave never cleared" - the status
            // line used to show only the overall TTL, never how many of a LIVE wave's creatures remain, nor
            // that a wave has its own two clocks (WaveClockExpired) that can end the run well before the TTL
            // does. Both only apply to a live wave of the Waves shape - never during the breather between
            // waves (NextWaveDueUtc != null), where today's plain clause and the overall TTL still say
            // everything there is to say.
            if (encounter.Type == MlDigsiteType.WavesAndMiniBoss && encounter.NextWaveDueUtc == null)
            {
                waveLeft = encounter.LiveWaveCount;

                var nearestKind = MlDigsiteRules.NearestWaveClock(now - encounter.WaveStartedUtc, now - encounter.LastKillUtc,
                    waveTimeLimit, stallTimeout, out var waveClockRemaining);

                // The three clocks a live wave is actually racing: the TTL (timeLeft, already computed above),
                // the wave time limit and the stall timeout. A zero-or-less tunable disables a clock and
                // excludes it here too, matching NearestWaveClock's own semantics for the other two. Ties go
                // to the TTL, so the stall clock only ever gets credit (and its extra sentence) when it is
                // STRICTLY the nearest of the three.
                var ttlEnabled = ttl > TimeSpan.Zero;

                if (nearestKind != MlDigsiteWaveClockKind.None && (!ttlEnabled || waveClockRemaining < timeLeft))
                {
                    timeLeft = waveClockRemaining;

                    if (nearestKind == MlDigsiteWaveClockKind.Stall)
                    {
                        extra = string.IsNullOrWhiteSpace(extra)
                            ? "A kill resets the clock."
                            : $"{extra} A kill resets the clock.";
                    }
                }
            }

            return MlDigsiteRules.StatusLine(encounter.Type, encounter.WavesSpawned, tierPct, encounter.CorruptedKills,
                bossHpPct, timeLeft, extra, corruptionKillsRequired, waveLeft);
        }

        /// <summary>
        /// What the owner's chest would pay if the owner BAILED right now, as a whole percent - through the
        /// same rule the payout itself uses, so the status line and the bail prompt can never promise something
        /// Deliver would not pay. Priced as a bail because that is the end the owner controls: before any wave
        /// is cleared it reads 0%, even though a wipe at that point would still pay the wave-1 floor.
        /// </summary>
        public static int CurrentTierPercent(MlDigsiteEncounter encounter)
        {
            var result = encounter.Type == MlDigsiteType.CorruptionMeter ? MlDigsiteResult.Failed : MlDigsiteResult.Scored;

            return (int)Math.Round(100.0 * MlDigsiteRules.EncounterPayoutFraction(encounter.Type,
                encounter.PayoutSnapshot(result, assumeBailed: true), MlDigsiteTunables.Payout));
        }

        // ---- /digsite bail -----------------------------------------------------------------------------

        /// <summary>The live encounter <paramref name="player"/> dug and that has not already been asked to end, or null.</summary>
        public static MlDigsiteEncounter FindOwnedEncounter(Player player)
        {
            if (player == null)
                return null;

            var guid = player.Guid.Full;

            return LiveEncounters.FirstOrDefault(e => e.DiggerGuid == guid && !e.EndRequested);
        }

        /// <summary>
        /// The Yes answer to /digsite bail. RE-VALIDATES from scratch - the dialog can stand for up to 30 s - and
        /// then only REQUESTS the end (Bailed); the next tick finishes it and pays the progress reached, times
        /// the fail multiplier (round 16). A bailed Corruption encounter pays its Corrupted-mob kill count the
        /// same way a bailed Waves encounter pays its wave tier. Returns a message for the caller, or null when
        /// the nearby announcement already says it all.
        /// </summary>
        public static string RequestBail(Player player, uint encounterId)
        {
            if (player == null)
                return null;

            if (!encounters.TryGetValue(encounterId, out var encounter) || encounter.State == MlDigsiteState.Ended)
                return "That digsite encounter has already ended.";

            if (encounter.DiggerGuid != player.Guid.Full)
                return MlDigsiteRules.BailRefusal(false, encounter.DiggerName);

            var result = encounter.Type == MlDigsiteType.CorruptionMeter ? MlDigsiteResult.Failed : MlDigsiteResult.Scored;

            if (!encounter.TryRequestEnd(MlDigsiteRules.EndReasons.Bailed, result))
                return "That digsite encounter is already ending.";

            log.Info($"[ML_DIGSITE] {encounter} bailed by {player.Name}");

            Announce(encounter, $"{player.Name} calls off the dig. The cache gives up what was earned.");

            return null;
        }

        /// <summary>
        /// The live encounter <paramref name="player"/> currently counts as a participant of (same instance,
        /// within the audience radius of its anchor), or null when they are not at one. Backs the /digsite
        /// command; uses the same radius <see cref="MlDigsiteAudience"/> uses for everything else, so "am I
        /// participating" never disagrees with what actually sizes and pays a fight.
        /// </summary>
        public static MlDigsiteEncounter FindParticipantEncounter(Player player)
        {
            if (player?.Location == null)
                return null;

            var radius = MlDigsiteTunables.AudienceRadiusMetres;

            foreach (var encounter in LiveEncounters)
            {
                var anchor = encounter.Anchor;

                if (anchor == null || player.Location.Instance != anchor.Instance)
                    continue;

                if (player.Location.DistanceTo(anchor) <= radius)
                    return encounter;
            }

            return null;
        }

        // ---- Waves: 8 fixed waves, with checkpoints --------------------------------------------------------

        /// <summary>
        /// Round 16: a FIXED 8-wave run, not endless. A cleared field schedules the next wave after
        /// ml_digsite_inter_wave_seconds (the death hook arms it), each wave bigger and harder than the last.
        /// Clearing wave MlDigsiteRules.TotalWaves (8) is a WIN, requested from the wave-clear tail of
        /// OnEncounterCreatureDied (not from this method - DriveWaves never sees the run end that way, only
        /// the OTHER ends below). Short of that, the run still ends by /digsite bail, a wipe, the TTL, or one
        /// of the two wave clocks below, and pays the tier it reached (MlDigsiteRules.WaveTierFraction) times
        /// the fail multiplier (MlDigsiteRules.EncounterPayoutFraction) - never 100%, since none of those is a
        /// win.
        ///
        /// The two wave clocks are the shipped Proving Grounds wave values (weenie 1001550): a 300 s per-wave
        /// limit and a 150 s stall timeout. They are checked only while a wave is live (no next wave
        /// scheduled), and EITHER running out now ENDS the encounter as WaveTimeLimit or Stalled. There is no
        /// forced mini-boss ENDING a run any more and no reduced tier: the tier reached is simply what is paid.
        ///
        /// ORDER MATTERS HERE, and it is not left to the reader. The tick resolves to exactly one
        /// MlDigsiteWavesTick (MlDigsiteRules.WavesTick) BEFORE anything is placed, and that same value is
        /// what gates the time-forced checkpoint mini-boss at the bottom of this method
        /// (MlDigsiteRules.ShouldForceCheckpoint). That is what stops a forced mini-boss being summoned onto
        /// the very tick the clocks tear the encounter down - which is what the first version of this code
        /// did, by asking for the forced spawn before it had asked the clocks anything.
        /// </summary>
        private static void DriveWaves(MlDigsiteEncounter encounter, DateTime now)
        {
            // While a wave is scheduled the clocks are not consulted at all: the field is clear, and the
            // breather is the game's own pause, not a stall. WavesTick enforces that too.
            var due = encounter.NextWaveDueUtc;
            var waveScheduled = due != null;

            string reason = null;

            var clockExpired = !waveScheduled
                && MlDigsiteRules.WaveClockExpired(now - encounter.WaveStartedUtc, now - encounter.LastKillUtc,
                    MlDigsiteTunables.WaveTimeLimit, MlDigsiteTunables.StallTimeout, out reason);

            var tick = MlDigsiteRules.WavesTick(waveScheduled, waveScheduled && now >= due.Value, clockExpired);

            if (tick == MlDigsiteWavesTick.SpawnWave)
                SpawnNextWave(encounter, NewRandom());

            if (tick == MlDigsiteWavesTick.EndRun)
            {
                if (encounter.TryRequestEnd(reason, MlDigsiteResult.Scored))
                {
                    log.Info($"[ML_DIGSITE] {encounter} wave {encounter.WavesSpawned} ran out its clock ({reason}); ending at the tier reached");

                    // Round 17 tester feedback: a clock-ended run left no record of what was still standing,
                    // so "killed everything on radar" could never be checked against what the encounter
                    // itself thought was alive. One line per survivor, logged here rather than at Finish/
                    // Deliver, because this is the one branch that knows the run is ending FOR a clock rather
                    // than a clear.
                    foreach (var survivor in encounter.LiveWaveSnapshot())
                        log.Info(SurvivorLogLine(encounter, survivor));

                    Announce(encounter, reason == MlDigsiteRules.EndReasons.Stalled
                        ? "The dig falls quiet as the fight goes out of it. The cache gives up what you earned."
                        : "The earth closes over the last of them before they can be put down. The cache gives up what you earned.");
                }

                // Returns whether or not THIS call won the request: either way the run is on its way out, and
                // nothing more should be placed into it on this tick.
                return;
            }

            // Last, and only on a tick that has already established the run continues. The forced mini-boss
            // may still arrive DURING a wave being fought - that is the point of it - but never onto the tick
            // that ends one.
            DriveForcedCheckpoint(encounter, now, tick);
        }

        /// <summary>
        /// One log line for a creature still standing when a wave clock tore the run down (round 17 tester
        /// feedback - see the EndRun branch of <see cref="DriveWaves"/>). Reports exactly what the encounter
        /// itself can see about the creature, so a report of "I killed them all" can be checked against what
        /// was actually still alive, where, and why.
        ///
        /// Distance is to the encounter's own anchor (<see cref="MlDigsiteEncounter.Anchor"/>) - the same
        /// point every other digsite distance check (MlDigsiteAudience) measures against - and is reported as
        /// "?" rather than a number whenever it cannot be trusted: no landblock (never entered/already left
        /// the world) or a different instance than the anchor's, the same two guards MlDigsiteAudience.Counts
        /// applies before it ever calls DistanceTo.
        /// </summary>
        private static string SurvivorLogLine(MlDigsiteEncounter encounter, Creature survivor)
        {
            var loc = survivor.Location;

            string dist;

            if (survivor.CurrentLandblock == null || loc == null || loc.Instance != encounter.Anchor.Instance)
                dist = "?";
            else
                dist = $"{loc.DistanceTo(encounter.Anchor):0.0} m";

            return $"[ML_DIGSITE] {encounter} survivor 0x{survivor.Guid.Full:X8} wcid={survivor.WeenieClassId} "
                + $"name=\"{survivor.Name}\" dead={survivor.IsDead} destroyed={survivor.IsDestroyed} "
                + $"inWorld={survivor.CurrentLandblock != null} loc={(loc == null ? "?" : loc.ToLOCString())} dist={dist}";
        }

        /// <summary>
        /// ROUND 14 FEEDBACK ("the waves never end - spawn a mini boss after 3 minutes"), predating round 16's
        /// fixed 8-wave cap: once a Waves run has been going for ml_digsite_forced_miniboss_seconds, place ONE
        /// checkpoint mini-boss whatever wave it is on, instead of waiting for the wave-count cadence
        /// (ml_digsite_checkpoint_every) to come round.
        ///
        /// This EXTENDS the checkpoint mechanism rather than adding a parallel one, so everything already
        /// true of a checkpoint stays true here: it is tracked outside the live wave and never holds one
        /// open, its death is not a win (it adds one checkpoint kill to the tier), and it may take the
        /// encounter's single Kept Siraluun roll if that has not been claimed yet. It is a SEPARATE mechanism
        /// from the round 16 win condition - clearing wave MlDigsiteRules.TotalWaves (8) - and does not
        /// itself end anything: a Waves run still ends by clearing wave 8, /digsite bail, a wipe, the TTL, or
        /// a wave clock, whichever comes first.
        ///
        /// FIRES EXACTLY ONCE. The elapsed-time test goes on being true for the rest of the run, so the
        /// one-shot latch (MlDigsiteEncounter.TryClaimForcedCheckpoint) is what stops a mini-boss per tick,
        /// and it is claimed only on the tick that actually spawns. A checkpoint already standing defers the
        /// forced one rather than consuming the latch: stacking a second mini-boss on the first is the one
        /// outcome SpawnNextWave's own checkpoint path is explicitly written to avoid.
        ///
        /// A THIN EXECUTOR, deliberately. Every condition - the elapsed-time threshold, the standing
        /// checkpoint, and the tick that is about to end the run - lives in the one pure predicate
        /// MlDigsiteRules.ShouldForceCheckpoint, so the gate can be unit-tested and so this method cannot
        /// grow a fourth condition that only the manager knows about. <paramref name="tick"/> is the caller's
        /// already-resolved MlDigsiteWavesTick; there is no overload that omits it, which is what makes it
        /// impossible to ask for a forced spawn without first having asked the wave clocks.
        /// </summary>
        private static void DriveForcedCheckpoint(MlDigsiteEncounter encounter, DateTime now, MlDigsiteWavesTick tick)
        {
            if (!MlDigsiteRules.ShouldForceCheckpoint(tick, now - encounter.StartedUtc,
                    MlDigsiteTunables.ForcedCheckpointAfter, encounter.CheckpointAlive))
            {
                return;
            }

            if (!encounter.TryClaimForcedCheckpoint())
                return;

            // Floored at 1 because waveNumber drives the per-wave scaling in TrySpawn, and a Waves encounter
            // that somehow reached here before its opening wave was recorded must not scale from zero.
            var waveNumber = Math.Max(1, encounter.WavesSpawned);

            if (MlDigsiteSpawner.TrySpawn(encounter, MlDigsiteRole.Checkpoint, NewRandom(), waveNumber) == null)
            {
                log.Warn($"[ML_DIGSITE] {encounter} forced checkpoint mini-boss failed to place at wave {waveNumber}");
                return;
            }

            log.Info($"[ML_DIGSITE] {encounter} forced a checkpoint mini-boss at wave {waveNumber} after {MlDigsiteTunables.ForcedCheckpointAfter.TotalSeconds:0}s");

            Announce(encounter, "The dig has been open too long. Something that has been listening to it climbs out.",
                ChatMessageType.WorldBroadcast);
        }

        /// <summary>
        /// Places the next wave, sized to the audience and to how deep the run is, restarts both wave clocks,
        /// and - on a checkpoint wave - places a checkpoint mini-boss ALONGSIDE it.
        ///
        /// <paramref name="announceStart"/> is false only for the opening spawn from
        /// <see cref="OpenEncounter"/>: that spawn's own type-specific opening line (OpeningLine, sent right
        /// after) already tells players a wave/field just went up, so a "Wave 1 begins." line under it would
        /// be redundant, not additive. Every later wave from <see cref="DriveWaves"/> still announces.
        ///
        /// THE CHECKPOINT MINI-BOSS (every ml_digsite_checkpoint_every'th wave of a Waves encounter) is tracked
        /// OUTSIDE the live wave (MlDigsiteEncounter.TrackCheckpoint), so it never holds the wave open, and its
        /// death is not a win - it adds one checkpoint kill to the tier. The per-wave latch
        /// (TryClaimCheckpoint) means a wave can only ever claim its checkpoint once. If the PREVIOUS
        /// checkpoint mini-boss is still standing, the new one is skipped rather than stacked on top of it.
        /// </summary>
        private static int SpawnNextWave(MlDigsiteEncounter encounter, Random rng, bool announceStart = true)
        {
            var waveNumber = encounter.WavesSpawned + 1;

            var participants = MlDigsiteAudience.Count(encounter.Anchor, MlDigsiteTunables.AudienceRadiusMetres);

            var count = MlDigsiteRules.WaveSpawnCount(MlDigsiteTunables.WaveCountBase, MlDigsiteTunables.WaveCountPerWave,
                waveNumber, participants, MlDigsiteTunables.WaveCountPerParticipant, MlDigsiteTunables.WaveCountCap);

            // Re-sampled per wave rather than fixed at the start, the same way a world event re-samples per
            // wave pick: people arrive at a fight in progress, and a wave sized at the moment the hole opened
            // would not notice.
            var placed = MlDigsiteSpawner.SpawnWave(encounter, count, rng, waveNumber);

            encounter.NoteWaveSpawned(DateTime.UtcNow);

            log.Info($"[ML_DIGSITE] {encounter} wave {waveNumber} placed={placed} (asked {count}, participants {participants})");

            if (placed > 0 && announceStart)
                Announce(encounter, MlDigsiteRules.WaveStartLine(waveNumber));

            if (encounter.Type == MlDigsiteType.WavesAndMiniBoss
                && MlDigsiteRules.IsCheckpointWave(waveNumber, MlDigsiteTunables.CheckpointEvery)
                && encounter.TryClaimCheckpoint(waveNumber))
            {
                if (encounter.CheckpointAlive)
                {
                    log.Info($"[ML_DIGSITE] {encounter} wave {waveNumber} checkpoint skipped: the previous checkpoint mini-boss is still alive");
                }
                else if (MlDigsiteSpawner.TrySpawn(encounter, MlDigsiteRole.Checkpoint, rng, waveNumber) != null)
                {
                    placed++;
                    Announce(encounter, "Something heavier shoulders its way up through the loose earth alongside them.", ChatMessageType.WorldBroadcast);
                }
            }

            return placed;
        }

        // ---- Corruption -----------------------------------------------------------------------------------

        /// <summary>
        /// Round 16 redesign. The meter is retired: one Corrupted mob is alive at a time (the objective, role
        /// Priority), tougher than the field around it; killing ml_digsite_corruption_kills_required of them -
        /// the NEXT one spawning only once the previous is dead - wins the encounter outright.
        ///
        /// Killing the Corrupted mob, and spawning the next one, is normally event-driven from
        /// OnEncounterCreatureDied - there is no polling for that half, unlike the old meter. TryRespawnPendingCorrupted
        /// is the one exception: it retries a spawn EVERY tick when one is owed and failed (code review fix -
        /// see MlDigsiteEncounter's pending-respawn remarks), so a transient placement failure degrades to a
        /// short delay instead of a run with no win path that idles to its TTL.
        ///
        /// The rest of this method drives the Corrupted mob's power gain (round 17, DriveCorruptionPower), the
        /// field's own growth (more mobs every ml_digsite_corruption_field_spawn_seconds, capped at
        /// MlDigsiteRules.MaxWaveSpawnCount live bodies) and the Corrupted mob's visual-tag re-broadcast on the
        /// same cadence as the growth.
        /// </summary>
        private static void DriveCorruption(MlDigsiteEncounter encounter, DateTime now)
        {
            TryRespawnPendingCorrupted(encounter);

            DriveCorruptionPower(encounter, now);

            if (!encounter.TryClaimFieldSpawnTick(now, MlDigsiteTunables.CorruptionFieldSpawnInterval))
                return;

            SpawnMoreField(encounter);
            ReannounceObjectiveScript(encounter);
        }

        /// <summary>
        /// ROUND 17 OWNER RULING: while a Corrupted mob is alive, the rest of the field gains
        /// ml_digsite_corruption_power_per_tick more damage every ml_digsite_corruption_power_tick_seconds, and
        /// EVERY gain is announced to the participants. This re-wires MlDigsiteRules.DamageRatingFor, which had
        /// no production caller after round 16 retired the meter; the cadence (5 s) and step (+3%) are the
        /// retired meter's own shipped values (ml_digsite_meter_tick_seconds / ml_digsite_meter_damage_step at
        /// 7e3f29534's parent), and the field write is the retired HardenField pass restored.
        ///
        /// Keyed to ONE Corrupted mob (MlDigsiteEncounter.TryClaimCorruptionPowerTick): its death resets the
        /// stacks and writes the field back to its base rating (OnCorruptedDied), so each of the three
        /// sequential Corrupted mobs starts from nothing. Field creatures spawned while a bonus is active get
        /// it at once (SpawnMoreField). The Corrupted mob itself never gains - it is the objective, not the
        /// field, and LiveWaveSnapshot does not contain it.
        /// </summary>
        private static void DriveCorruptionPower(MlDigsiteEncounter encounter, DateTime now)
        {
            var perTick = MlDigsiteTunables.CorruptionPowerPerTick;

            if (!(perTick > 0.0))
                return;

            var corrupted = encounter.ObjectiveCreature;
            var alive = corrupted != null && !corrupted.IsDestroyed && !corrupted.IsDead;

            if (!encounter.TryClaimCorruptionPowerTick(alive ? corrupted.Guid.Full : 0, alive, now,
                    MlDigsiteTunables.CorruptionPowerTickInterval, out var stacks))
            {
                return;
            }

            var before = MlDigsiteRules.CorruptionPowerPercent(stacks - 1, perTick);
            var after = MlDigsiteRules.CorruptionPowerPercent(stacks, perTick);

            // At the growth cap the stack count still climbs but nothing gets stronger: no write, no line.
            if (!MlDigsiteRules.ShouldAnnounceCorruptionGain(before, after))
                return;

            HardenField(encounter, perTick, onlyWhileRunning: true);

            log.Info($"[ML_DIGSITE] {encounter} corruption power gain {stacks} from Corrupted 0x{corrupted.Guid.Full:X8}: field now +{after}% damage");

            Announce(encounter, MlDigsiteRules.CorruptionPowerLine(after), ChatMessageType.WorldBroadcast);
        }

        /// <summary>
        /// Sets every live field creature's DamageRating to what the encounter's CURRENT power-gain stack count
        /// entitles it to, computed from the creature's BASE rating (MlDigsiteEncounter.BaseDamageRating), never from
        /// its current one - so a missed write costs nothing and nothing compounds. With the stacks at 0 it writes
        /// the base back, which is how a reset is applied.
        ///
        /// Written on each creature's OWN landblock action queue (the same discipline the retired meter's
        /// HardenField and WorldEventSpawner.ApplyBossDamageRating use): this runs on the world thread, or on
        /// the landblock thread that killed a Corrupted mob, and neither owns the field's landblock. It takes
        /// effect on the creature's very next swing, because Creature_Rating.GetDamageRating() is read fresh by
        /// DamageEvent on every attack. In-memory only - an encounter creature never persists.
        ///
        /// <paramref name="onlyWhileRunning"/> is false only for the end-of-encounter reset, which runs after
        /// MarkEnded has already made RunValid false.
        /// </summary>
        private static void HardenField(MlDigsiteEncounter encounter, double perTick, bool onlyWhileRunning)
        {
            foreach (var creature in encounter.LiveWaveSnapshot())
            {
                if (creature == null || creature.IsDestroyed || creature.IsDead)
                    continue;

                var landblock = creature.CurrentLandblock;

                if (landblock == null)
                    continue;

                var target = creature;

                landblock.EnqueueAction(new ActionEventDelegate(() =>
                {
                    try
                    {
                        if (target.IsDestroyed || target.IsDead)
                            return;

                        if (onlyWhileRunning && !encounter.RunValid)
                            return;

                        var baseRating = encounter.BaseDamageRating(target.Guid.Full, target.DamageRating ?? 0);

                        // The stack count is read HERE, when the write runs, not when it was queued: a reset queued
                        // behind a gain (or a gain behind a reset) then always lands on the current value.
                        target.DamageRating = MlDigsiteRules.DamageRatingFor(baseRating, encounter.CorruptionPowerStacks, perTick);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[ML_DIGSITE] {encounter} corruption power write threw for 0x{target.Guid.Full:X8}", ex);
                    }
                }));
            }
        }

        /// <summary>
        /// Round 16 code-review fix. Retries a Corrupted mob spawn every tick while
        /// MlDigsiteRules.ShouldRetryCorruptedSpawn says one is owed - a spawn that failed either at
        /// OpenEncounter or in OnCorruptedDied (MlDigsiteSpawner.TrySpawn can legitimately return null: a
        /// roster miss, a scatter-point/terrain snap failure, EnterWorld failing, or the held list already
        /// closed under a race with Finish). The predicate's own `!objectiveAlive` half is what guarantees
        /// this can never place a second Corrupted mob alongside a live one. A success clears the debt inside
        /// TrySpawn -> TrackObjective; a repeated failure simply marks it pending again and this runs once
        /// more next tick, until the run ends (Tick stops calling Drive at all once an end is requested).
        /// </summary>
        private static void TryRespawnPendingCorrupted(MlDigsiteEncounter encounter)
        {
            if (!MlDigsiteRules.ShouldRetryCorruptedSpawn(encounter.CorruptedSpawnPending, encounter.ObjectiveAlive))
                return;

            if (MlDigsiteSpawner.TrySpawn(encounter, MlDigsiteRole.Priority, NewRandom()) != null)
                log.Info($"[ML_DIGSITE] {encounter} retried the pending Corrupted mob spawn and placed it");
        }

        /// <summary>
        /// Places up to ml_digsite_corruption_field_spawn_count more field creatures, capped by whatever room
        /// is left under MlDigsiteRules.MaxWaveSpawnCount live bodies - the same landblock safety ceiling the
        /// Waves shape's per-wave spawn count is capped at. Never announced beyond the ordinary WorldBroadcast
        /// a placed creature does not get (field creatures are ambient, like an ordinary wave).
        /// </summary>
        private static void SpawnMoreField(MlDigsiteEncounter encounter)
        {
            var room = MlDigsiteRules.MaxWaveSpawnCount - encounter.LiveWaveCount;

            var count = Math.Min(room, MlDigsiteTunables.CorruptionFieldSpawnCount);

            if (count <= 0)
                return;

            var placed = MlDigsiteSpawner.SpawnWave(encounter, count, NewRandom(), waveNumber: 1);

            if (placed > 0)
            {
                log.Info($"[ML_DIGSITE] {encounter} corruption field reinforcement placed={placed} (asked {count})");
                Announce(encounter, "More of the corrupted field claws its way up out of the ground.");

                // Round 17: reinforcements arrive already carrying whatever power the live Corrupted mob has
                // built up, rather than waiting up to one power interval to catch up. The write is absolute
                // and idempotent, so re-writing the creatures that already had it costs nothing.
                if (encounter.CorruptionPowerStacks > 0)
                    HardenField(encounter, MlDigsiteTunables.CorruptionPowerPerTick, onlyWhileRunning: true);
            }
        }

        /// <summary>
        /// Re-broadcasts the Corrupted mob's visual tag script on the same cadence the field grows on (round
        /// 13 feedback item F, round 16: recadenced off the now-retired meter tick). The stamp already goes
        /// out once in the object's own CreateObject packet (MlDigsiteSpawner.TrySpawn sets DefaultScriptId
        /// before EnterWorld), but a player who is out of range when the creature spawns and walks into range
        /// later never received that packet - a GameMessageScript is a one-shot play command any client in
        /// range at send time will render, so this closes that gap.
        ///
        /// Written on the creature's OWN landblock action queue: this runs on the world thread, which owns no
        /// landblock.
        /// </summary>
        private static void ReannounceObjectiveScript(MlDigsiteEncounter encounter)
        {
            if (encounter.Type != MlDigsiteType.CorruptionMeter)
                return;

            var creature = encounter.ObjectiveCreature;

            if (creature == null || creature.IsDestroyed || creature.IsDead)
                return;

            var landblock = creature.CurrentLandblock;

            if (landblock == null)
                return;

            var target = creature;
            var script = (PlayScript)MlDigsiteTunables.PriorityScriptId;

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (target.IsDestroyed || target.IsDead || !encounter.RunValid)
                        return;

                    target.EnqueueBroadcast(new GameMessageScript(target.Guid, script));
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {encounter} priority script re-broadcast threw for 0x{target.Guid.Full:X8}", ex);
                }
            }));
        }

        // ---- death reporting ----------------------------------------------------------------------------

        /// <summary>
        /// Reports one encounter creature's death. Called from Creature.Die behind the TWO-KEY check
        /// (invariant 2): the in-memory back-reference AND the persisted stamp, never a wcid test - the
        /// roster draws ordinary island wildlife, so a wcid check would fire for every wild Carenzi on Marae
        /// Lassel.
        ///
        /// The reference check below is the third key: a creature whose encounter has already been removed
        /// from the registry, or replaced by a later one under the same id, is inert.
        ///
        /// THIS HOOK NEVER FINISHES ANYTHING. It runs on a landblock thread, and it only records: the death,
        /// the dead creature's damage credit, and a Kept Siraluun's resolved drop, all in ONE call
        /// (MlDigsiteEncounter.NoteCreatureDeath) that refuses once the encounter has ended - that single
        /// critical section is what guarantees a recorded feather is always in the list Deliver later reads.
        /// A win REQUESTS the end; the world-thread tick finishes it (see the class remarks).
        /// </summary>
        public static void OnEncounterCreatureDied(Creature creature)
        {
            var encounter = creature?.P_DigsiteEncounter;

            if (encounter == null)
                return;

            if (!encounters.TryGetValue(encounter.EncounterId, out var live) || !ReferenceEquals(live, encounter))
                return;

            var now = DateTime.UtcNow;

            // Resolved BEFORE the encounter's lock is taken (it rolls the RNG and walks the create list), and
            // handed in so it is recorded atomically with the death itself.
            var drops = ResolveKeptSiraluunDrops(encounter, creature);

            var kind = encounter.NoteCreatureDeath(creature.Guid.Full, now, creature, drops, out var waveNowEmpty);

            if (kind == MlDigsiteDeathKind.None)
                return;

            // Round 17 tester feedback: the survivor log at a clock-ended run is only half the picture without
            // a record of what WAS killed and when, so the two can be read side by side.
            log.Info($"[ML_DIGSITE] {encounter} death 0x{creature.Guid.Full:X8} wcid={creature.WeenieClassId} "
                + $"kind={kind} waveLeft={encounter.LiveWaveCount}");

            if (drops != null)
            {
                foreach (var drop in drops)
                    log.Info($"[ML_DIGSITE] {encounter} Kept Siraluun wcid={creature.WeenieClassId} (0x{creature.Guid.Full:X8}) banked create_list drop wcid={drop.WeenieClassId} stackSize={drop.StackSize} for the chest");
            }

            if (kind == MlDigsiteDeathKind.Objective)
            {
                if (encounter.Type == MlDigsiteType.CorruptionMeter)
                {
                    OnCorruptedDied(encounter, now);
                    return;
                }

                // A win for Boss Rush (the boss). A Waves encounter has no objective at all - it wins by
                // clearing wave 8 instead (handled in the wave-clear tail below).
                //
                // Announced only when this request is the one that counts: a bail or a wipe that got in first
                // owns the end, and a victory line under it would be a lie.
                if (encounter.TryRequestEnd(MlDigsiteRules.EndReasons.ObjectiveKilled, MlDigsiteResult.FullClear))
                    Announce(encounter, "The dig goes quiet. Whatever was buried here is finished.");

                return;
            }

            if (kind == MlDigsiteDeathKind.Checkpoint)
            {
                // Not a win: the waves keep coming. The kill has already been counted toward the tier inside
                // NoteCreatureDeath.
                Announce(encounter, "The heavier thing falls, and the cache grows richer for it.");
                return;
            }

            if (kind == MlDigsiteDeathKind.Add)
            {
                // A Boss Rush mechanic add. Worth nothing to the payout and never a win - NoteCreatureDeath
                // deliberately moves no tier bookkeeping for it - so the only thing that reacts is the
                // driver: a volatile add arms its fuse, an immune phase's add set shrinks by one.
                MlDigsiteBossMechanics.OnAddDied(encounter, creature);
                return;
            }

            // A cleared wave arms the breather before the next one - whether or not a checkpoint mini-boss is
            // still standing, which is exactly why it is tracked outside the live wave. Only the Waves shape
            // paces this way; a Corruption encounter's field grows on its own timer (DriveCorruption) and is
            // never "cleared".
            if (!waveNowEmpty || encounter.Type != MlDigsiteType.WavesAndMiniBoss)
                return;

            // Round 16: clearing wave TotalWaves (8) wins outright - a FullClear paying 100%, whatever the
            // tier table would have said. KNOWN NARROW RACE: TryRequestEnd's first-request-wins rule means a
            // bail or wipe that reached the encounter's lock a moment earlier still owns the end - the win
            // request below then simply fails and the encounter pays as whatever end got there first instead
            // (wave-7's tier via WaveTierFraction's "last row at or below" lookup, times the fail multiplier),
            // which is a safe, merely slightly conservative payout rather than a broken one.
            if (MlDigsiteRules.WaveWon(encounter.HighestWaveCleared))
            {
                if (encounter.TryRequestEnd(MlDigsiteRules.EndReasons.WavesWon, MlDigsiteResult.FullClear))
                    Announce(encounter, $"The last of them falls. Wave {MlDigsiteRules.TotalWaves} is cleared - the dig is yours.", ChatMessageType.WorldBroadcast);

                return;
            }

            var breather = MlDigsiteTunables.InterWaveDelay;

            encounter.ScheduleNextWave(now + breather);

            // Round 14 feedback: tell them which wave they just finished and when the next one lands. Sent
            // only when a next wave is actually coming - an end already requested (a bail, a wipe, a clock)
            // means DriveWaves will never spawn the scheduled wave, so announcing its countdown would promise
            // something that is not going to happen.
            //
            // Round 15: green (WorldBroadcast), with the running count read from HighestWaveCleared - the number
            // the Waves payout tier is paid on - rather than WavesSpawned.
            if (!encounter.EndRequested)
                Announce(encounter, MlDigsiteRules.WaveClearedLine(encounter.HighestWaveCleared, breather), ChatMessageType.WorldBroadcast);
        }

        /// <summary>
        /// Round 16: one Corrupted mob (the Corruption shape's objective) has died. Either this was the
        /// required'th kill - a WIN, requested and announced like every other objective kill - or it was not,
        /// in which case the encounter's progress is announced and the NEXT Corrupted mob is placed at once
        /// (MlDigsiteRules: "the next Corrupted mob spawns only after the previous one dies", which this
        /// death hook is exactly the trigger for - there is no polling wait between kills).
        /// </summary>
        private static void OnCorruptedDied(MlDigsiteEncounter encounter, DateTime now)
        {
            var required = MlDigsiteTunables.CorruptionKillsRequired;
            var kills = encounter.CorruptedKills;

            // Round 17: the power this Corrupted mob built up dies with it. Reset BEFORE anything else so the
            // next Corrupted mob starts from zero whatever happens below, and write the field back to its base
            // rating (a no-op pass when no gain was ever applied).
            var droppedStacks = encounter.ResetCorruptionPower();

            if (droppedStacks > 0)
            {
                HardenField(encounter, MlDigsiteTunables.CorruptionPowerPerTick, onlyWhileRunning: true);
                log.Info($"[ML_DIGSITE] {encounter} Corrupted mob died; {droppedStacks} corruption power gain(s) removed from the field");
            }

            if (MlDigsiteRules.CorruptionKillsWon(kills, required))
            {
                if (encounter.TryRequestEnd(MlDigsiteRules.EndReasons.CorruptionWon, MlDigsiteResult.FullClear))
                    Announce(encounter, "The last of the corruption is put down. The dig goes quiet.", ChatMessageType.WorldBroadcast);

                return;
            }

            Announce(encounter, $"Corrupted slain: {kills} of {required}.", ChatMessageType.WorldBroadcast);

            if (encounter.EndRequested)
                return;

            if (MlDigsiteSpawner.TrySpawn(encounter, MlDigsiteRole.Priority, NewRandom()) == null)
            {
                // Code-review fix: this used to be a dead end - a log line and nothing else, which left the
                // encounter with no live objective and no win path. Marking the debt here means
                // DriveCorruption's retry (MlDigsiteRules.ShouldRetryCorruptedSpawn) picks it up on the very
                // next 1s tick instead of the run idling to its TTL.
                log.Warn($"[ML_DIGSITE] {encounter} failed to place the next Corrupted mob after kill {kills} of {required}; will retry every tick");
                encounter.MarkCorruptedSpawnPending();
            }
        }

        /// <summary>
        /// Every Kept Siraluun wcid, for the O(1) membership test <see cref="ResolveKeptSiraluunDrops"/> needs
        /// on every digsite death. Built once from MlDigsiteRoster.KeptSiraluun rather than walked per call.
        /// </summary>
        private static readonly HashSet<uint> keptSiraluunWcids =
            new HashSet<uint>(MlDigsiteRoster.KeptSiraluun.Select(e => e.Wcid));

        /// <summary>
        /// Resolves a dying Kept Siraluun's create_list drop, restricted to Kept Siraluun ONLY - an ordinary
        /// Drumtaken mini-boss stays corpseless with no drop, exactly as designed. See
        /// MlDigsiteRules.ResolveCreateListDrops for why this is needed at all: a digsite creature never gets a
        /// corpse, so GenerateTreasure - the only engine path that ever reads a create_list - never runs for it.
        ///
        /// Resolves only; it records nothing. The caller hands the result to MlDigsiteEncounter.
        /// NoteCreatureDeath, which records it atomically with the death (see that method for the ordering
        /// argument). Returns null for anything that is not a Kept Siraluun.
        ///
        /// Never throws: a creature is mid-Die() when this runs, and a failure here must cost a chest item,
        /// never the kill itself.
        /// </summary>
        private static IReadOnlyList<PropertiesCreateList> ResolveKeptSiraluunDrops(MlDigsiteEncounter encounter, Creature creature)
        {
            if (!keptSiraluunWcids.Contains(creature.WeenieClassId))
                return null;

            try
            {
                var rows = DeathTreasureCreateListRows(creature);
                var drops = MlDigsiteRules.ResolveCreateListDrops(rows, () => ThreadSafeRandom.Next(0.0f, 1.0f));

                if (drops.Count == 0)
                    log.Debug($"[ML_DIGSITE] {encounter} Kept Siraluun wcid={creature.WeenieClassId} (0x{creature.Guid.Full:X8}) died with no create_list drop selected");

                return drops;
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} Kept Siraluun create_list resolution threw for wcid={creature.WeenieClassId} (0x{creature.Guid.Full:X8})", ex);
                return null;
            }
        }

        /// <summary>
        /// A creature's create_list rows, filtered to the SAME non-wielded contain/treasure destination
        /// GenerateTreasure itself reads (Creature_Death.cs:949-950): Contain, or Treasure without Wield.
        /// Read from the live creature's Biota, which is still intact at this point in Die() - the corpse
        /// step that would otherwise consume it has not run yet and never will for a digsite creature.
        /// </summary>
        private static List<PropertiesCreateList> DeathTreasureCreateListRows(Creature creature)
        {
            var rows = new List<PropertiesCreateList>();

            var createList = creature.Biota?.PropertiesCreateList;

            if (createList == null)
                return rows;

            foreach (var row in createList)
            {
                var isDeathTreasureRow = (row.DestinationType & DestinationType.Contain) != 0 ||
                    ((row.DestinationType & DestinationType.Treasure) != 0 && (row.DestinationType & DestinationType.Wield) == 0);

                if (isDeathTreasureRow)
                    rows.Add(row);
            }

            return rows;
        }

        // ---- the single exit path -----------------------------------------------------------------------

        /// <summary>
        /// THE one way an encounter ends, whatever ended it (invariant 4), and PRIVATE: its only caller is
        /// <see cref="Tick"/>, consuming a request, on the world thread. Everything else requests
        /// (MlDigsiteEncounter.TryRequestEnd). Idempotent through <see cref="MlDigsiteEncounter.MarkEnded"/>,
        /// which returns true to exactly one caller.
        ///
        /// ORDER: MarkEnded first, so no further death (and no further Kept Siraluun drop) can be recorded;
        /// then Deliver, which reads the recorded drops and claims the reward latch; cleanup last.
        ///
        /// CLEANUP IS IN A finally, and that is the invariant: a throw from the announcement or from the
        /// reward must never strand an encounter's creatures in the world with TimeToRot = -1 and nothing
        /// left to destroy them.
        /// </summary>
        private static void Finish(MlDigsiteEncounter encounter, MlDigsiteResult result, string reason)
        {
            if (encounter == null || !encounter.MarkEnded(reason, out _))
                return;

            // Dropped from the registry now rather than at the next reap, so the slot it held against the
            // concurrent cap is free immediately and a re-issued id cannot collide with it.
            encounters.TryRemove(encounter.EncounterId, out _);

            log.Info($"[ML_DIGSITE] ended {encounter} result={result} reason={reason}");

            try
            {
                // Group-reward bookkeeping the payout reads, completed now that nothing can die into the
                // ledger any more (MarkEnded above): damage dealt to creatures still ALIVE (a boss never
                // killed, a checkpoint mini-boss still standing, the wave in progress) - safe to read here
                // because the landblock tick has joined - and one last presence stamp, so a helper who
                // arrived since the last 15 s reap is not missed.
                CreditSurvivors(encounter);

                var anchor = encounter.Anchor;

                if (anchor != null && LandblockManager.IsLoaded(anchor.LandblockId, anchor.Instance))
                    encounter.MarkPlayersPresent(PresentPlayerGuids(anchor), DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} end-of-fight credit pass threw; paying from what was already recorded", ex);
            }

            try
            {
                // Round 17: the corruption power bonus is removed from every creature at encounter end. The
                // field is destroyed by DestroyHeld right after this, and the reset write is queued on each
                // creature's landblock AHEAD of that destroy, so it lands first; anything left dying keeps its
                // base rating rather than the bonus.
                if (encounter.ResetCorruptionPower() > 0)
                    HardenField(encounter, MlDigsiteTunables.CorruptionPowerPerTick, onlyWhileRunning: false);
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} end-of-fight corruption power reset threw", ex);
            }

            try
            {
                MlDigsiteRewards.Deliver(encounter, result);
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} reward delivery threw", ex);
            }
            finally
            {
                DestroyHeld(encounter);
            }
        }

        /// <summary>
        /// Credits the damage history of every held creature that is still alive, so a group that wiped on a
        /// boss it never killed is still credited for the damage it did. Dead ones were already credited at
        /// their death (MlDigsiteEncounter.NoteCreatureDeath), so they are skipped rather than counted twice.
        /// </summary>
        private static void CreditSurvivors(MlDigsiteEncounter encounter)
        {
            foreach (var wo in encounter.HeldSnapshot())
            {
                if (wo is Creature creature && !creature.IsDead && !creature.IsDestroyed)
                    encounter.CreditDamage(creature);
            }
        }

        /// <summary>The guids of the live audience at <paramref name="anchor"/> (MlDigsiteAudience: alive, staff included).</summary>
        private static IEnumerable<uint> PresentPlayerGuids(Position anchor)
            => MlDigsiteAudience.Participants(anchor, MlDigsiteTunables.AudienceRadiusMetres).Select(p => p.Guid.Full);

        /// <summary>
        /// Takes everything the encounter placed back out of the world, then closes the held list so nothing
        /// can be adopted afterwards.
        ///
        /// Each object is destroyed on ITS OWN landblock's action queue rather than inline, because this runs
        /// on the world thread (Finish is only ever called from the tick), which owns no landblock.
        ///
        /// A creature that is already dead or dying is left ALONE: Creature.Die queues a delayed chain that
        /// builds the corpse once the death animation finishes, and destroying it out from under that chain
        /// would corpse an object already removed from the landblock. The back-reference is nulled for every
        /// creature regardless, including a dying one, so nothing can report a death to a finished encounter.
        /// </summary>
        private static void DestroyHeld(MlDigsiteEncounter encounter)
        {
            // FIRST, before anything is taken out of the world: a pending mechanic step would otherwise fire
            // at a position whose objects have just been destroyed, or apply damage on behalf of a boss that
            // no longer exists. Stopping the driver is a complete stop - the step queue is the only scheduler
            // the mechanics have.
            MlDigsiteBossMechanics.Stop(encounter);

            var held = encounter.HeldSnapshot();

            // Closed BEFORE the destroy pass rather than after, so a spawn still in flight on some landblock
            // queue is refused by AddHeld and destroys itself, instead of landing in the world just after
            // this pass walked past it.
            encounter.CloseHeld();

            foreach (var wo in held)
            {
                try
                {
                    if (wo is Creature creature)
                    {
                        creature.P_DigsiteEncounter = null;

                        // Cleared with the back-reference, so a boss left standing by a dead or dying edge
                        // case can never be an immortal monster nothing can switch back on.
                        creature.P_DigsiteImmune = false;

                        if (creature.IsDead)
                            continue;
                    }

                    if (wo.IsDestroyed)
                        continue;

                    var landblock = wo.CurrentLandblock;
                    var target = wo;

                    if (landblock != null)
                        landblock.EnqueueAction(new ActionEventDelegate(() =>
                        {
                            if (!target.IsDestroyed)
                                target.Destroy();
                        }));
                    else
                        target.Destroy();
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {encounter} cleanup threw for 0x{wo?.Guid.Full:X8}", ex);
                }
            }
        }

        // ---- helpers ------------------------------------------------------------------------------------

        /// <summary>
        /// Sends one line to everyone at the digsite - NEARBY PLAYERS ONLY, never a server-wide broadcast. A
        /// digsite is ambient: dozens can be running, and a global line per band crossing would be spam.
        /// OWNER RULING (2026-09-24) retired the one exception this used to have (the two realm-wide opening
        /// lines, round 17's AnnounceOpening) - every digsite line, including the opening one, now goes
        /// through here and stays scoped to the audience radius.
        ///
        /// <paramref name="type"/> defaults to the ordinary ambient chat colour. A line announcing a wave,
        /// mini-boss, boss or priority mob SPAWNING is sent as WorldBroadcast (0x14) instead, which the client
        /// renders pale green (ACE.Entity.Enum.ChatMessageType.cs:264-269) - the same colour every other
        /// spawn-arrival line in this fork uses to stand out from ordinary encounter narration.
        ///
        /// INTERNAL rather than private only so the Boss Rush mechanic modules can reach it: it is still THE
        /// one routing point for every digsite chat line, and no module builds its own audience loop
        /// (MlDigsiteMechanicContext.Say and .Tell are the only callers outside this file).
        /// </summary>
        internal static void Announce(MlDigsiteEncounter encounter, string message, ChatMessageType type = ChatMessageType.Broadcast)
        {
            if (encounter?.Anchor == null || string.IsNullOrEmpty(message))
                return;

            try
            {
                foreach (var player in MlDigsiteAudience.Participants(encounter.Anchor, MlDigsiteTunables.AudienceRadiusMetres))
                    player.Session?.Network.EnqueueSend(new GameMessageSystemChat(message, type));
            }
            catch (Exception ex)
            {
                // An announcement is never allowed to be the thing that breaks an encounter.
                log.Error($"[ML_DIGSITE] {encounter} announcement threw", ex);
            }
        }

        /// <summary>
        /// int.MaxValue is excluded because ThreadSafeRandom.Next(int, int) is INCLUSIVE of its upper bound
        /// and computes max + 1 internally, which overflows there. Same idiom as WorldEventGeometry.
        /// </summary>
        private static Random NewRandom() => new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));

        private static bool WithinSeparation(Position a, Position b)
        {
            if (a == null || b == null)
                return false;

            // Instance first: two encounters at the same physical coordinates in different realm copies of
            // the landblock are not near each other at all, they are in different places.
            if (a.Instance != b.Instance)
                return false;

            return a.DistanceTo(b) < MlDigsiteTunables.SeparationMetres;
        }
    }
}
