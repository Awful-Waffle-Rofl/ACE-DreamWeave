using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.WorldObjects
{
    // WaffleACE wave challenge ("The Proving Grounds (Wave)"): a portal (PropertyInt.WaveChallengeWaves > 0) drops
    // the player into a strictly single-player ephemeral dungeon and runs a wave-by-wave gauntlet. The engine
    // spawns wave 1 after a short countdown; when EVERY creature in the live wave is dead the wave is cleared,
    // and after a short breather the next wave spawns. The run ends on player death, on all waves being cleared,
    // on a stall (no damage dealt to the live wave for StallTimeout seconds), on the live wave outlasting its
    // absolute WaveTimeLimit, or on forfeit (exit portal, any other teleport out of the instance, or a mid-run
    // logout caught at next login).
    // Score = (waves FULLY cleared * 100) + the whole-percent of the live wave's total health destroyed when the
    // run ended (0 if the run ended exactly on a clear boundary), stored in hundredths of a wave ("centi-waves")
    // so a death mid-wave still credits partial progress. Persisted incrementally: every clear banks
    // clearedWaves*100 immediately, and run end additionally banks the partial-wave fraction if it beats that.
    //
    // Waves are content, not code: the portal names a roster base wcid, and the roster weenie for wave N is
    // (base + N - 1). A roster weenie is inert - the engine reads its generator table as the wave's spawn list
    // (wcid + absolute cell/origin/angles per row) and places each entry itself, so no in-world generator object
    // exists and nothing respawns.
    //
    // This is the third sibling of Player_DpsChallenge.cs and Player_SurvivalChallenge.cs and mirrors them
    // deliberately: the same run-instance binding to invalidate stale ticks, the same persisted-active-flag +
    // login-clear forfeit model, the same two-flag death split (the penalty skips key off the death-in-progress
    // marker, never the active flag, because the run is consumed up front), and the same EphemeralRealmExitTo
    // return with a 3 second grace.
    partial class Player
    {
        /// <summary>
        /// How often the stall watchdog samples the live wave's damage total. The stall THRESHOLD is the portal's
        /// WaveChallengeStallTimeout; this is only the sampling rate, so the effective detection latency is up to
        /// one tick beyond the threshold.
        /// </summary>
        private const double WaveChallengeStallTickSeconds = 30;

        /// <summary>Seconds of countdown between arrival and wave 1 spawning (the 5-4-3-2-1 pattern).</summary>
        private const int WaveChallengeCountdownSeconds = 5;

        // Instance id the current run is bound to; 0 = no active run. Every staged ActionChain segment (countdown,
        // inter-wave delay, stall tick) guards on this plus the player's live Location.Instance, so a run that was
        // abandoned - the player left the instance, died out of it, or logged out - simply stops.
        private uint waveChallengeRunInstance;

        // Run configuration, captured from the portal at arrival.
        private int waveChallengeTotalWaves;
        private uint waveChallengeRosterBaseWcid;
        private double waveChallengeInterWaveDelay;
        private double waveChallengeStallTimeout;
        private double waveChallengeWaveTimeLimit;

        // The wave currently live (0 = none spawned yet), and the highest wave fully cleared this run (the score).
        private int waveChallengeWave;
        private int waveChallengeClearedWaves;

        // Server-wide #1 entry's (score, tiebreak) pair sampled ONCE at run start, using the "wave" board's own
        // Score/TieBreaker so the pair can never disagree with what /top would show as #1. The record check at
        // the end compares against this snapshot rather than a fresh scan, because this player's own best is
        // raised incrementally during the run - a fresh scan would include their own just-persisted score and no
        // run could ever beat it. waveChallengeServerMaxTieBreakAtRunStart is the tiebreak among only the
        // candidates AT that max score (not an independent max across everyone), so the pair matches the actual
        // top-ranked entry Rank() would produce, not two different players' values stitched together.
        private long waveChallengeServerMaxAtRunStart;
        private long waveChallengeServerMaxTieBreakAtRunStart;

        // This player's own BestWaveScore sampled at run start, for the same reason: the incremental per-wave
        // persist raises BestWaveScore mid-run, so "did this run beat my best?" can only be asked of the snapshot.
        private long waveChallengePersonalBestAtRunStart;

        // Unix time (seconds) the run's clock started: the moment wave 1 was actually summoned, not the moment
        // the player arrived (the 5-4-3-2-1 countdown does not count against clear time). 0 = clock not running,
        // which a partial run (death, stall, timeout, forfeit) simply leaves at - only a full clear reads it.
        private double waveChallengeClearClockStart;

        // This run's own clear time in centiseconds, set by RecordWaveClearTime on a full clear whether or not it
        // beats the player's stored personal best. Reset at run start. Kept separate from BestWaveClearTimeCenti
        // because that property only gets written on a personal best, so a full clear that is #1 on the BOARD but
        // not a personal best (impossible today since #1 implies a personal best, but kept independent so the two
        // concerns - "did I beat my own best" and "did I take the board's #1 spot" - can never entangle) would
        // otherwise have nothing to compare against.
        private long? waveChallengeThisRunClearTimeCenti;

        // Captured at the start of the death sequence: true iff this death is a valid in-arena wave death. The
        // death-penalty skips key off this rather than WaveChallengeActive, because scoring clears the active flag
        // up front (before CalculateDeathItems / ThreadSafeTeleportOnDeath run later in the chain).
        private bool waveChallengeDeathInProgress;

        // Guids of the live wave's creatures that are still alive. A wave is cleared when this empties.
        private readonly HashSet<uint> waveChallengeAliveGuids = new HashSet<uint>();

        // The live wave's creature references, held from the spawn loop so the stall watchdog can read their
        // DamageHistory without ever scanning the landblock (landblock scans miss pendingAdditions).
        private readonly List<Creature> waveChallengeLiveCreatures = new List<Creature>();

        // Each live-wave creature's max health at spawn, keyed by guid, captured once so a partial-wave fraction
        // at run end can be computed against the wave's ORIGINAL total rather than whatever is left standing.
        private readonly Dictionary<uint, uint> waveChallengeSpawnMaxHealth = new Dictionary<uint, uint>();

        // Stall watchdog state: the last observed damage total across the live wave, and when it last changed.
        private double waveChallengeLastDamageTotal;
        private double waveChallengeLastDamageChangeTime;

        /// <summary>
        /// This player's best wave-gauntlet score in hundredths of a wave. Persisted on the character biota.
        /// </summary>
        public long BestWaveScoreCenti
        {
            get => GetBestWaveScoreCenti(this);
            set => SetProperty(PropertyInt64.BestWaveScoreCenti, value);
        }

        /// <summary>
        /// Reads a player's best wave-gauntlet score in hundredths of a wave, falling back to the legacy
        /// whole-wave BestWaveScore (9021) scaled by 100 for a character who has never written the new property.
        /// Static (rather than an instance member) so /top and the admin reset can read it off any IPlayer.
        /// </summary>
        public static long GetBestWaveScoreCenti(IPlayer p)
        {
            return p.GetProperty(PropertyInt64.BestWaveScoreCenti) ?? (p.GetProperty(PropertyInt64.BestWaveScore) ?? 0) * 100;
        }

        /// <summary>
        /// Formats a centi-wave score (e.g. 1237) as "Wave 12.37".
        /// </summary>
        public static string FormatWaveScore(long centi) => $"Wave {centi / 100}.{centi % 100:D2}";

        /// <summary>
        /// This player's best FULL-CLEAR time of the wave gauntlet, in centiseconds. Null (never written) for
        /// every character who cleared before this shipped - there is no historical run-clock data to backfill,
        /// so a null here means "no time recorded", not "0 seconds". Persisted on the character biota.
        /// </summary>
        public long? BestWaveClearTimeCenti
        {
            get => GetProperty(PropertyInt64.BestWaveClearTimeCenti);
            set
            {
                if (value.HasValue)
                    SetProperty(PropertyInt64.BestWaveClearTimeCenti, value.Value);
                else
                    RemoveProperty(PropertyInt64.BestWaveClearTimeCenti);
            }
        }

        /// <summary>
        /// Pure comparison for the wave-gauntlet world-record announcement. Compares this run's (score, tiebreak)
        /// pair against a run-start snapshot's pair, lexicographically - score first, then tiebreak - mirroring
        /// LeaderboardRanking.Boards's "wave" board ordering exactly (score desc, then tiebreak desc, where a
        /// faster clear time sorts higher and a missing time sorts lowest via long.MinValue). This is what lets a
        /// new #1 won purely on a faster clear time (an EQUAL, capped full-clear score) announce: score alone
        /// cannot distinguish it, but the tiebreak can. A tie (equal score AND equal tiebreak) does not announce.
        /// No property reads - callers own getting the snapshot and this run's own values.
        /// </summary>
        public static bool IsNewWaveRecord(long thisRunScore, long? thisRunClearTimeCenti, long snapshotScore, long snapshotTieBreak)
        {
            if (thisRunScore <= 0)
                return false;

            if (thisRunScore != snapshotScore)
                return thisRunScore > snapshotScore;

            var thisRunTieBreak = thisRunClearTimeCenti.HasValue && thisRunClearTimeCenti.Value > 0 ? -thisRunClearTimeCenti.Value : long.MinValue;

            return thisRunTieBreak > snapshotTieBreak;
        }

        /// <summary>
        /// Formats a centi-second duration (e.g. 75423) as "12:34.23" (m:ss.cc). Minutes are not zero-padded, the
        /// way the rest of /top renders durations.
        /// </summary>
        public static string FormatWaveClearTime(long centi)
        {
            var totalCentiseconds = Math.Max(0, centi);
            var minutes = totalCentiseconds / 6000;
            var seconds = (totalCentiseconds / 100) % 60;
            var hundredths = totalCentiseconds % 100;
            return $"{minutes}:{seconds:D2}.{hundredths:D2}";
        }

        /// <summary>
        /// True while a wave-challenge run is armed/in progress. Persisted so a mid-run logout can be caught at
        /// next login (see WorldManager.DoPlayerEnterWorld) and the player re-homed to their lifestone.
        /// </summary>
        public bool WaveChallengeActive
        {
            get => GetProperty(PropertyBool.WaveChallengeActive) ?? false;
            set => SetProperty(PropertyBool.WaveChallengeActive, value);
        }

        private void WaveChallengeMsg(string message)
        {
            Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.System));
        }

        /// <summary>
        /// True if the run bound to <paramref name="runInstance"/> is still valid for this player: the run must be
        /// armed for that exact instance and the player must still be standing in it.
        /// </summary>
        private bool WaveChallengeRunValid(uint runInstance)
        {
            return runInstance != 0
                && waveChallengeRunInstance == runInstance
                && CurrentLandblock != null
                && Location != null
                && Location.Instance == runInstance;
        }

        /// <summary>
        /// True while the player is armed for a wave run AND still standing in the bound run instance.
        /// </summary>
        public bool IsInWaveChallengeInstance =>
            WaveChallengeActive
            && waveChallengeRunInstance != 0
            && Location != null
            && Location.Instance == waveChallengeRunInstance;

        /// <summary>
        /// Starts the wave-gauntlet run. Called on arrival in the arena instance (from Portal.ActOnUse's
        /// teleport-completion follow-up), so the gauntlet effectively begins when the player lands. Binds the run
        /// to the player's current instance, snapshots the server record, and counts wave 1 in.
        /// </summary>
        public void StartWaveChallenge(int totalWaves, uint rosterBaseWcid, double interWaveDelay, double stallTimeout, double waveTimeLimit)
        {
            // Mule (WaffleACE): a mule cannot fight, so it has no business in a Proving Grounds run.
            if (MuleBlocked(MuleAction.StartChallenge))
                return;

            // PvP template (progression lock): no challenge run while templated.
            var templateRefusal = PvpTemplateBlocked(PvpTemplateAction.Challenge);
            if (templateRefusal != null)
            {
                SendPvpTemplateRefusal(templateRefusal);
                return;
            }

            if (Location == null || totalWaves <= 0 || rosterBaseWcid == 0)
            {
                // A misconfigured wave portal (no roster base wcid, no waves) arms the persisted flag and then
                // lands here. The flag is cleared harmlessly by CheckWaveChallengeInstanceExit's unbound-run
                // branch on the very next teleport reconciliation, but the content bug must not be silent.
                log.Error($"[WAVE] {Name} (0x{Guid}) - StartWaveChallenge no-opped: totalWaves {totalWaves}, rosterBaseWcid {rosterBaseWcid}. Check the wave portal's WaveChallengeWaves / WaveChallengeRosterBaseWcid.");
                return;
            }

            var runInstance = Location.Instance;
            waveChallengeRunInstance = runInstance;
            waveChallengeTotalWaves = totalWaves;
            waveChallengeRosterBaseWcid = rosterBaseWcid;
            waveChallengeInterWaveDelay = interWaveDelay > 0 ? interWaveDelay : 0;
            waveChallengeStallTimeout = stallTimeout > 0 ? stallTimeout : 0;
            waveChallengeWaveTimeLimit = waveTimeLimit > 0 ? waveTimeLimit : 0;
            waveChallengeWave = 0;
            waveChallengeClearedWaves = 0;
            waveChallengeAliveGuids.Clear();
            waveChallengeLiveCreatures.Clear();
            waveChallengeSpawnMaxHealth.Clear();
            waveChallengeClearClockStart = 0;
            waveChallengeThisRunClearTimeCenti = null;
            WaveChallengeActive = true;
            RushNextPlayerSave(5);

            // Snapshot the server record BEFORE the run banks anything of its own (same online+offline scan the
            // /top command uses). This is what lets the reigning record-holder beat their own record: their stored
            // best is still the old value here, and the per-wave incremental persist during the run cannot poison
            // the comparison.
            // Staff (WaffleACE): exempt players are excluded from the record bar real players are measured
            // against, so a staff run cannot poison the snapshot this run's own new-record check reads at the end.
            // The wave board's own Score/TieBreaker delegates are used (not a hand-copied formula) so this
            // snapshot can never disagree with what /top would show as #1.
            var waveBoard = LeaderboardRanking.Boards.First(b => b.Key == "wave");
            var startExemptNames = LeaderboardExemptionManager.GetExemptAccountNames();
            var startCandidates = PlayerManager.GetAllPlayers()
                .Where(p => !LeaderboardExemptionManager.IsExempt(p, startExemptNames))
                .ToList();

            waveChallengeServerMaxAtRunStart = startCandidates
                .Select(waveBoard.Score)
                .DefaultIfEmpty(0)
                .Max();

            // Tiebreak among only the candidates AT the max score - not an independent max across everyone - so
            // the pair matches the actual top-ranked entry Rank() would produce.
            waveChallengeServerMaxTieBreakAtRunStart = startCandidates
                .Where(p => waveBoard.Score(p) == waveChallengeServerMaxAtRunStart)
                .Select(waveBoard.TieBreaker)
                .DefaultIfEmpty(long.MinValue)
                .Max();

            waveChallengePersonalBestAtRunStart = BestWaveScoreCenti;

            WaveChallengeMsg($"The gauntlet begins! {totalWaves} waves stand between you and the end.");

            // per-second countdown (5, 4, 3, 2, 1) then wave 1, mirroring the Attack arena's final countdown
            var chain = new ActionChain();

            for (var secondsLeft = WaveChallengeCountdownSeconds; secondsLeft >= 1; secondsLeft--)
            {
                var tick = secondsLeft;
                chain.AddDelaySeconds(1);
                chain.AddAction(this, () => { if (WaveChallengeRunValid(runInstance)) WaveChallengeMsg($"{tick}..."); });
            }

            chain.AddDelaySeconds(1);
            chain.AddAction(this, () =>
            {
                if (!WaveChallengeRunValid(runInstance))
                    return;

                // the clear-time clock starts the instant wave 1 is actually summoned, not at arrival - the
                // countdown itself must not count against clear time
                waveChallengeClearClockStart = Time.GetUnixTime();

                SpawnWave(1);

                // the stall watchdog self-terminates the instant the run is no longer valid, so it is safe to arm
                // even if the wave 1 spawn just failed and ended the run
                ScheduleWaveStallTick(runInstance);
            });

            chain.EnqueueChain();
        }

        /// <summary>
        /// Spawns one wave from its roster weenie's generator table. The roster is addressed by wcid arithmetic
        /// (base + N - 1); each generator row contributes one creature at an absolute cell/origin/angles, placed
        /// directly into the run instance. Fails CLOSED: if nothing at all made it into the world, the run ends
        /// rather than leaving the player in an empty arena that can never advance.
        /// </summary>
        private void SpawnWave(int waveNumber)
        {
            var runInstance = waveChallengeRunInstance;

            if (!WaveChallengeRunValid(runInstance))
                return;

            var rosterWcid = waveChallengeRosterBaseWcid + (uint)(waveNumber - 1);
            var roster = DatabaseManager.World.GetCachedWeenie(rosterWcid);

            if (roster == null)
            {
                log.Error($"[WAVE] {Name} (0x{Guid}) - wave {waveNumber} roster weenie {rosterWcid} does not exist; ending the run");
                FinishWaveChallengeRun($"The gauntlet falters - wave {waveNumber} could not be summoned. The trial ends.", teleportOut: true);
                return;
            }

            // Sanity net only: a roster that declares a different wave index than the one being spawned means the
            // content's wcid block is misaligned. Log it loudly and carry on - the wcid arithmetic is authoritative.
            var declaredIndex = roster.GetProperty(PropertyInt.WaveChallengeWaveIndex);
            if (declaredIndex != null && declaredIndex.Value != waveNumber)
                log.Error($"[WAVE] {Name} (0x{Guid}) - roster weenie {rosterWcid} declares WaveChallengeWaveIndex {declaredIndex.Value} but is being spawned as wave {waveNumber}; the roster wcid block is misaligned");

            waveChallengeWave = waveNumber;
            waveChallengeAliveGuids.Clear();
            waveChallengeLiveCreatures.Clear();
            waveChallengeSpawnMaxHealth.Clear();

            // READ-ONLY pass over the roster weenie's generator rows. These collections come straight out of the
            // world weenie cache and are SHARED by every consumer of that weenie - mutating a row here would
            // corrupt the global cached weenie (see the clone-before-write warning in Landblock.CreateWorldObjects).
            var rows = roster.PropertiesGenerator;

            if (rows != null)
            {
                foreach (var row in rows)
                {
                    if (row.WeenieClassId == 0)
                        continue;

                    var wo = WorldObjectFactory.CreateNewWorldObject(row.WeenieClassId);

                    if (wo == null)
                    {
                        log.Error($"[WAVE] {Name} (0x{Guid}) - wave {waveNumber} roster {rosterWcid}: failed to create wcid {row.WeenieClassId}");
                        continue;
                    }

                    // Fail closed on a non-Creature roster row, consistent with this method's contract. Only a
                    // Creature can die, so a non-Creature counted toward the wave would hold that wave open for
                    // the rest of the run - the player would have nothing left to kill and would only ever exit
                    // via a watchdog. Log it loudly and drop the object rather than placing something unkillable
                    // in the arena.
                    if (!(wo is Creature creature))
                    {
                        log.Error($"[WAVE] {Name} (0x{Guid}) - wave {waveNumber} roster {rosterWcid}: wcid {row.WeenieClassId} is not a Creature; it can never die, so it is NOT counted toward the wave and was dropped");
                        wo.Destroy();
                        continue;
                    }

                    // absolute placement, mirroring GeneratorProfile.Spawn_Specific's ObjCellId branch, but bound
                    // to this run's ephemeral instance rather than a generator's
                    wo.Location = new Position(row.ObjCellId ?? 0, row.OriginX ?? 0, row.OriginY ?? 0, row.OriginZ ?? 0,
                        row.AnglesX ?? 0, row.AnglesY ?? 0, row.AnglesZ ?? 0, row.AnglesW ?? 0, runInstance);

                    // same landblock verify Spawn_Specific does: a row pointing at another landblock would drag a
                    // creature outside the private instance entirely
                    if (wo.Location.InstancedLandblock != Location.InstancedLandblock)
                    {
                        log.Error($"[WAVE] {Name} (0x{Guid}) - wave {waveNumber} roster {rosterWcid}: wcid {row.WeenieClassId} cell 0x{row.ObjCellId ?? 0:X8} is not in the arena landblock; skipped");
                        wo.Destroy();
                        continue;
                    }

                    if (!wo.EnterWorld())
                    {
                        log.Error($"[WAVE] {Name} (0x{Guid}) - wave {waveNumber} roster {rosterWcid}: wcid {row.WeenieClassId} failed to enter the world at {wo.Location.ToLOCString()}");
                        wo.Destroy();
                        continue;
                    }

                    // OPT OUT OF DECAY. A wave creature is built by hand, so it gets a DYNAMIC guid and has no
                    // Generator back-reference - which is exactly what IsDecayable() tests (the
                    // "if (IsGenerator || Generator != null) return false;" guard at WorldObject_Decay.cs:34 is
                    // what exempts ordinary dungeon monsters, and a wave creature fails it). The landblock
                    // heartbeat would then rot it at DefaultTimeToRot (5 minutes) with a bare Destroy() that never
                    // runs Die(), so OnWaveCreatureDied would never fire, the guid would sit in
                    // waveChallengeAliveGuids forever, and the wave could never clear - the player just watches
                    // the wave blink out with no corpse and then eats a watchdog timeout. -1 means never rot;
                    // Pet.cs:87 is the existing precedent for a hand-spawned creature opting out this way. How
                    // long a wave may live is now an explicit, engine-owned limit (see ArmWaveTimeLimit).
                    wo.TimeToRot = -1;

                    // only a creature that is actually in the world counts toward the wave
                    waveChallengeAliveGuids.Add(creature.Guid.Full);

                    creature.P_WaveOwner = this;
                    waveChallengeLiveCreatures.Add(creature);
                    waveChallengeSpawnMaxHealth[creature.Guid.Full] = creature.Health.MaxValue;

                    // without the flag the creature's death never reports back, so the wave can only ever end
                    // via a watchdog - a content bug worth shouting about
                    if (creature.GetProperty(PropertyBool.WaveChallengeCreature) != true)
                        log.Error($"[WAVE] {Name} (0x{Guid}) - wave {waveNumber} roster {rosterWcid}: wcid {row.WeenieClassId} is missing PropertyBool.WaveChallengeCreature; its death will not advance the wave");
                }
            }

            if (waveChallengeAliveGuids.Count == 0)
            {
                log.Error($"[WAVE] {Name} (0x{Guid}) - wave {waveNumber} roster {rosterWcid} put nothing into the world; ending the run rather than advancing");
                FinishWaveChallengeRun($"The gauntlet falters - nothing answered the call for wave {waveNumber}. The trial ends.", teleportOut: true);
                return;
            }

            // rearm the stall watchdog for the new wave, so the inter-wave breather never counts against it
            waveChallengeLastDamageTotal = GetWaveDamageTotal();
            waveChallengeLastDamageChangeTime = Time.GetUnixTime();

            WaveChallengeMsg($"Wave {waveNumber} of {waveChallengeTotalWaves} - {waveChallengeAliveGuids.Count} enemies!");

            // arm this wave's absolute deadline (the stall watchdog is a separate, damage-based guard)
            ArmWaveTimeLimit(runInstance, waveNumber);
        }

        /// <summary>
        /// Arms the ABSOLUTE time limit for one wave: staged warnings plus a final expiry, all on a single
        /// ActionChain, modelled on the DPS arena's staging helper. Complements rather than replaces the stall
        /// watchdog: the stall watchdog ends a run where the player is dealing NO damage, this one ends a run
        /// where the player is fighting but simply cannot finish the wave in time.
        /// <para/>
        /// Every staged segment guards on BOTH the run instance AND the wave number it was armed for. The wave
        /// guard is load-bearing: a wave cleared before its deadline leaves this chain running, and without it
        /// those stale segments would warn during - and then END - a later, perfectly healthy wave.
        /// </summary>
        private void ArmWaveTimeLimit(uint runInstance, int waveNumber)
        {
            var limit = waveChallengeWaveTimeLimit;

            if (limit <= 0)   // <= 0 means "no limit", same convention as waveChallengeStallTimeout
                return;

            var chain = new ActionChain();
            double elapsed = 0;

            // Adds a segment that fires at absolute time 'at' seconds into the wave, converting successive
            // absolute times into the relative delays an ActionChain actually takes.
            void Stage(double at, Action action)
            {
                var delta = at - elapsed;
                if (delta < 0)
                    delta = 0;
                chain.AddDelaySeconds(delta);
                elapsed = at;
                chain.AddAction(this, action);
            }

            // Each warning is only scheduled if it genuinely fits inside the limit, so a short configured limit
            // degrades gracefully - no negative delays, and no warning doubling up with the expiry at t=0.
            if (limit > 120)
                Stage(limit - 120, () => { if (WaveTimeLimitStageValid(runInstance, waveNumber)) WaveChallengeMsg($"Wave {waveNumber}: 2 minutes remaining."); });

            if (limit > 60)
                Stage(limit - 60, () => { if (WaveTimeLimitStageValid(runInstance, waveNumber)) WaveChallengeMsg($"Wave {waveNumber}: 1 minute remaining."); });

            if (limit > 30)
                Stage(limit - 30, () => { if (WaveTimeLimitStageValid(runInstance, waveNumber)) WaveChallengeMsg($"Wave {waveNumber}: 30 seconds remaining!"); });

            Stage(limit, () =>
            {
                if (!WaveTimeLimitStageValid(runInstance, waveNumber))
                    return;

                FinishWaveChallengeRun($"Wave {waveNumber} has outlasted you. The gauntlet ends.", teleportOut: true);
            });

            chain.EnqueueChain();
        }

        /// <summary>
        /// True if a staged wave-deadline segment should still act: the run must still be valid for this player,
        /// AND the wave the segment was armed for must still be the live one.
        /// </summary>
        private bool WaveTimeLimitStageValid(uint runInstance, int waveNumber)
        {
            return WaveChallengeRunValid(runInstance) && waveChallengeWave == waveNumber;
        }

        /// <summary>
        /// Called from Creature.Die() for any creature flagged PropertyBool.WaveChallengeCreature, via the
        /// in-memory P_WaveOwner back-reference set at spawn. Removes the creature from the live wave and clears
        /// the wave once nothing is left standing. No-ops for a stale run (the player left, died, or logged out).
        /// </summary>
        public void OnWaveCreatureDied(Creature creature)
        {
            if (creature == null)
                return;

            if (!WaveChallengeRunValid(waveChallengeRunInstance))
                return;

            // not part of the live wave (a leftover from an earlier wave, or already counted)
            if (!waveChallengeAliveGuids.Remove(creature.Guid.Full))
                return;

            if (waveChallengeAliveGuids.Count > 0)
                return;

            OnWaveCleared(waveChallengeWave);
        }

        /// <summary>
        /// A wave is fully dead: bank the score, then either finish the gauntlet (last wave) or schedule the next
        /// wave after the inter-wave breather.
        /// </summary>
        private void OnWaveCleared(int waveNumber)
        {
            var runInstance = waveChallengeRunInstance;

            waveChallengeClearedWaves = Math.Max(waveChallengeClearedWaves, waveNumber);
            waveChallengeLiveCreatures.Clear();
            waveChallengeSpawnMaxHealth.Clear();

            // Bank the cleared wave IMMEDIATELY rather than only at run end, so a crash, a forfeit or a logout
            // still keeps the waves that were genuinely cleared. RushNextPlayerSave is self-throttling, so calling
            // it on every clear does not need batching.
            var clearedCenti = waveChallengeClearedWaves * 100L;
            if (clearedCenti > BestWaveScoreCenti)
            {
                BestWaveScoreCenti = clearedCenti;
                RushNextPlayerSave(5);
            }

            if (waveNumber >= waveChallengeTotalWaves)
            {
                WaveChallengeMsg($"Wave {waveNumber} cleared - the gauntlet is complete!");
                RecordWaveClearTime();
                FinishWaveChallengeRun("You have run the whole gauntlet. The Proving Grounds stand silent.", teleportOut: true);
                return;
            }

            WaveChallengeMsg($"Wave {waveNumber} cleared! Wave {waveNumber + 1} arrives in {waveChallengeInterWaveDelay:N0} seconds.");

            var chain = new ActionChain();
            chain.AddDelaySeconds(waveChallengeInterWaveDelay);
            chain.AddAction(this, () =>
            {
                if (!WaveChallengeRunValid(runInstance))
                    return;

                SpawnWave(waveNumber + 1);
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// Called only from the full-clear branch of OnWaveCleared. Computes this run's clear time from
        /// waveChallengeClearClockStart (armed the instant wave 1 was summoned), records it in
        /// waveChallengeThisRunClearTimeCenti (whether or not it beats the stored personal best - the run-end
        /// record check needs THIS run's time regardless), and if it beats the stored personal best (or none is
        /// stored) writes and persists it as well. A clock that never started (0, e.g. a StartWaveChallenge that
        /// raced a run-consuming reset) is treated as no time to record rather than crediting an absurd
        /// multi-decade duration.
        /// </summary>
        private void RecordWaveClearTime()
        {
            if (waveChallengeClearClockStart <= 0)
                return;

            var centi = (long)Math.Round((Time.GetUnixTime() - waveChallengeClearClockStart) * 100);

            if (centi <= 0)
                return;

            waveChallengeThisRunClearTimeCenti = centi;

            var priorBest = BestWaveClearTimeCenti;

            if (priorBest.HasValue && centi >= priorBest.Value)
            {
                WaveChallengeMsg($"Clear time: {FormatWaveClearTime(centi)}.");
                return;
            }

            BestWaveClearTimeCenti = centi;
            RushNextPlayerSave(5);

            WaveChallengeMsg($"Clear time: {FormatWaveClearTime(centi)} - new personal best!");
        }

        /// <summary>
        /// The stall watchdog: one self-rescheduling ActionChain that samples the damage dealt to the live wave.
        /// If that total has not moved for WaveChallengeStallTimeout seconds the run ends, scoring the last wave
        /// actually cleared. This one is deliberately damage-based - only ZERO progress trips it - so it reads as
        /// "the player has stopped fighting". The separate absolute per-wave deadline lives in ArmWaveTimeLimit;
        /// the two run side by side and neither auto-advances the gauntlet or kills the wave to clear it.
        /// </summary>
        private void ScheduleWaveStallTick(uint runInstance)
        {
            if (waveChallengeStallTimeout <= 0)
                return;

            var chain = new ActionChain();
            chain.AddDelaySeconds(WaveChallengeStallTickSeconds);
            chain.AddAction(this, () =>
            {
                if (!WaveChallengeRunValid(runInstance))
                    return;

                var damage = GetWaveDamageTotal();

                if (damage > waveChallengeLastDamageTotal)
                {
                    waveChallengeLastDamageTotal = damage;
                    waveChallengeLastDamageChangeTime = Time.GetUnixTime();
                }
                else if (Time.GetUnixTime() - waveChallengeLastDamageChangeTime >= waveChallengeStallTimeout)
                {
                    FinishWaveChallengeRun($"You have made no headway against wave {waveChallengeWave} for {waveChallengeStallTimeout:N0} seconds. The gauntlet ends.", teleportOut: true);
                    return;
                }

                ScheduleWaveStallTick(runInstance);
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// Total damage taken by every creature in the live wave, from the references captured at spawn. No
        /// landblock scan: GetAllWorldObjectsForDiagnostics misses pendingAdditions, which is exactly the window a
        /// freshly-spawned wave sits in.
        /// </summary>
        private double GetWaveDamageTotal()
        {
            double total = 0;

            foreach (var creature in waveChallengeLiveCreatures)
            {
                if (creature?.DamageHistory == null)
                    continue;

                foreach (var info in creature.DamageHistory.TotalDamage.Values)
                    total += info.TotalDamage;
            }

            return total;
        }

        /// <summary>
        /// Fraction (0..1) of the live wave's total spawn health that has been destroyed, for the partial-wave
        /// score credited at run end. A dead creature counts its full spawn max (it cannot deal any more damage
        /// to raise DamageHistory further); a living one counts net effective damage taken, clamped to its own
        /// max so a last-hit overkill cannot push the wave's total past 100%. Must be read BEFORE
        /// ClearRemainingWaveCreatures runs, and returns 0 once the wave itself is empty (nothing live to credit).
        /// </summary>
        private double GetLiveWaveFraction()
        {
            if (waveChallengeLiveCreatures.Count == 0)
                return 0;

            double sumMax = 0;
            double sumDealt = 0;

            foreach (var creature in waveChallengeLiveCreatures)
            {
                if (creature == null || !waveChallengeSpawnMaxHealth.TryGetValue(creature.Guid.Full, out var max) || max == 0)
                    continue;

                sumMax += max;

                double dealt;

                if (!waveChallengeAliveGuids.Contains(creature.Guid.Full))
                {
                    // dead: counts as fully destroyed regardless of what DamageHistory happens to total
                    dealt = max;
                }
                else if (creature.DamageHistory == null)
                {
                    dealt = 0;
                }
                else
                {
                    dealt = creature.DamageHistory.TotalDamage.Values.Sum(info => info.TotalDamage);
                }

                sumDealt += Math.Min(max, Math.Max(0, dealt));
            }

            if (sumMax <= 0)
                return 0;

            return Math.Min(1.0, Math.Max(0.0, sumDealt / sumMax));
        }

        /// <summary>
        /// The single run-end path, shared by death, victory, stall and every forfeit. Reports the score, does the
        /// one-and-only server-record evaluation (against the snapshot taken at run start), clears the arena, and
        /// consumes the run so nothing can re-score or re-advance it. BestWaveScore itself was already persisted
        /// incrementally by OnWaveCleared, so a run ending here never needs to bank anything.
        /// <para/>
        /// <paramref name="teleportOut"/> is false for the death path (Player_Death does its own teleport and
        /// needs EphemeralRealmExitTo left intact), false for the exit-portal path (the portal is teleporting the
        /// player already), and false for the post-teleport forfeit reconciliation (the player has left).
        /// </summary>
        private void FinishWaveChallengeRun(string message, bool teleportOut)
        {
            if (waveChallengeRunInstance == 0)
                return;

            // Partial credit only applies to a wave still in flight when the run ended - a run that just cleared
            // its last wave via OnWaveCleared has already set waveChallengeClearedWaves == waveChallengeWave, so
            // this is skipped and GetLiveWaveFraction (which needs the now-cleared live-creature list) is never
            // called. Capped at 99 so a partial wave can never tie or beat a full clear on the centi scale. Must
            // run BEFORE ClearRemainingWaveCreatures, which destroys the very creatures it reads.
            var partial = 0L;
            if (waveChallengeWave > waveChallengeClearedWaves)
                partial = Math.Min(99, (long)Math.Floor(GetLiveWaveFraction() * 100));

            var score = waveChallengeClearedWaves * 100L + partial;

            if (score > BestWaveScoreCenti)
                BestWaveScoreCenti = score;

            if (message != null)
                WaveChallengeMsg(message);

            if (score == 0)
            {
                WaveChallengeMsg("You cleared no waves.");
            }
            else
            {
                var suffix = partial > 0 ? $", and {partial}% of wave {waveChallengeWave}." : ".";
                WaveChallengeMsg($"You cleared {waveChallengeClearedWaves:N0} of {waveChallengeTotalWaves:N0} waves{suffix}");
            }

            // Server-record check, evaluated exactly ONCE per run against the run-start snapshot. A fresh scan here
            // would read this player's own incrementally-persisted best and could never be beaten. Comparing the
            // full (score, tiebreak) pair - not score alone - is what lets a #1 won purely on a faster clear time
            // (equal, capped full-clear score) announce.
            // Staff (WaffleACE): an exempt player's run does not trigger the world broadcast, even if it would
            // beat the (already staff-filtered) snapshot taken at run start. Read fresh here rather than reusing
            // the run-start snapshot's set, since a run can span long enough for the config to change mid-run.
            if (IsNewWaveRecord(score, waveChallengeThisRunClearTimeCenti, waveChallengeServerMaxAtRunStart, waveChallengeServerMaxTieBreakAtRunStart)
                && !LeaderboardExemptionManager.IsExempt(this))
            {
                string recordMsg;

                // Gated on the full clear alone, not on the clock having armed: a clock that never started (see
                // RecordWaveClearTime) still leaves waveChallengeClearedWaves at the full total, and that run is
                // still the #1 full clear even with no time to show for it.
                if (waveChallengeClearedWaves >= waveChallengeTotalWaves)
                {
                    var timeSuffix = waveChallengeThisRunClearTimeCenti.HasValue
                        ? $" in {FormatWaveClearTime(waveChallengeThisRunClearTimeCenti.Value)}"
                        : "";
                    recordMsg = $"[The Proving Grounds] {Name} has cleared all {waveChallengeTotalWaves:N0} waves of the gauntlet{timeSuffix} - a new record!";
                }
                else
                {
                    recordMsg = $"[The Proving Grounds] {Name} has fought through wave {score / 100}.{score % 100:D2} of the gauntlet - a new record!";
                }

                PlayerManager.BroadcastToAll(new GameMessageSystemChat(recordMsg, ChatMessageType.WorldBroadcast));
            }

            if (score > 0 && score > waveChallengePersonalBestAtRunStart)
                WaveChallengeMsg($"New personal best: {FormatWaveScore(score)}!");

            ClearRemainingWaveCreatures();

            Position dest = null;

            if (teleportOut)
            {
                var exitTo = GetPosition(PositionType.EphemeralRealmExitTo);
                dest = exitTo != null ? new Position(exitTo) : Sanctuary;
                SetPosition(PositionType.EphemeralRealmExitTo, null);

                // No EphemeralRealmExitTo AND no Sanctuary: the run below is still consumed, but the teleport at
                // the end of this method is skipped, so the player is left standing in a dead arena instance with
                // nothing left to fight and no automatic way out. Nothing else reports this, so say so here rather
                // than inventing a destination they never chose.
                if (dest == null)
                    log.Error($"[WAVE] {Name} (0x{Guid}) - run ended with teleportOut requested but no destination: EphemeralRealmExitTo is unset and Sanctuary is null. The player is stranded in the arena instance and must recall out.");
            }

            // consume the run (the death-penalty skips continue to key off waveChallengeDeathInProgress)
            waveChallengeRunInstance = 0;
            waveChallengeWave = 0;
            waveChallengeAliveGuids.Clear();
            waveChallengeLiveCreatures.Clear();
            waveChallengeSpawnMaxHealth.Clear();
            waveChallengeClearClockStart = 0;
            waveChallengeThisRunClearTimeCenti = null;
            WaveChallengeActive = false;
            RushNextPlayerSave(5);

            if (teleportOut && dest != null)
            {
                var exitChain = new ActionChain();
                exitChain.AddDelaySeconds(3);   // brief pause so the player can read the result
                exitChain.AddAction(this, () => Teleport(dest));   // Teleport self-validates the instance destination
                exitChain.EnqueueChain();
            }
        }

        /// <summary>
        /// Destroys whatever is left of the live wave. Only runs while the player is still standing in the run
        /// instance, so the destroys always happen on the arena landblock's own tick - a forfeit reconciled AFTER
        /// the player has teleported away skips this and simply lets the ephemeral landblock be torn down with its
        /// contents, rather than reaching into another landblock from this thread.
        /// </summary>
        private void ClearRemainingWaveCreatures()
        {
            if (Location == null || Location.Instance != waveChallengeRunInstance)
                return;

            foreach (var creature in waveChallengeLiveCreatures)
            {
                // Skip anything already dead or dying. Creature.Die() queues a delayed dieChain that builds the
                // corpse once the death animation finishes (Creature_Death.cs:148-156); destroying the creature
                // out from under that pending chain would have it corpse an object already removed from the
                // landblock. GeneratorProfile.DestroyAll takes the same guard for the same reason.
                if (creature == null || creature.IsDestroyed || creature.IsDead)
                    continue;

                creature.P_WaveOwner = null;
                creature.Destroy();
            }
        }

        /// <summary>
        /// True iff this player is dying inside a valid, active wave run - the gate for scoring the run and waiving
        /// the normal death penalties. Requires the persisted active flag AND that the player is still in the bound
        /// run instance, so a stale flag cannot turn an ordinary death into a penalty-free one.
        /// </summary>
        private bool IsWaveChallengeDeath()
        {
            return WaveChallengeActive
                && waveChallengeRunInstance != 0
                && Location != null
                && Location.Instance == waveChallengeRunInstance;
        }

        /// <summary>
        /// Death-sequence entry point (called from Player.Die()). If this is a valid in-arena wave death, it ends
        /// the run immediately and returns true so the caller waives the death penalties for the rest of the
        /// sequence; otherwise it does nothing and returns false. The score is the last wave fully cleared, which
        /// was already banked when that wave cleared.
        /// </summary>
        public bool TryBeginWaveChallengeDeath()
        {
            waveChallengeDeathInProgress = IsWaveChallengeDeath();

            if (waveChallengeDeathInProgress)
                FinishWaveChallengeRun("You have fallen. The gauntlet ends here.", teleportOut: false);

            return waveChallengeDeathInProgress;
        }

        /// <summary>
        /// True while the current death sequence is a wave-challenge death - read by the death-penalty skips in
        /// Player_Death.cs (vitae, enchantment purge, death items, teleport destination).
        /// </summary>
        public bool WaveChallengeDeathInProgress => waveChallengeDeathInProgress;

        /// <summary>
        /// Clears the wave-death marker once the death sequence has fully completed.
        /// </summary>
        public void EndWaveChallengeDeath()
        {
            waveChallengeDeathInProgress = false;
        }

        /// <summary>
        /// Exit-portal finish (called from Portal.ActOnUse when the in-arena exit portal is used mid-run). Ends the
        /// run keeping the waves already cleared, and marks it finished BEFORE the portal's teleport runs, so the
        /// subsequent OnTeleportComplete instance-exit check sees an already-finished run and does not re-fire.
        /// The portal does the teleport itself, so this must not consume EphemeralRealmExitTo.
        /// </summary>
        public void FinishWaveChallengeAtExit()
        {
            if (IsInWaveChallengeInstance)
                FinishWaveChallengeRun("You step out of the gauntlet.", teleportOut: false);
        }

        /// <summary>
        /// Reconciles the persisted run flag against the player's current instance after a teleport completes. Only
        /// death, the exit portal, and login-clear reset WaveChallengeActive; every OTHER way out of the arena
        /// (lifestone/portal recall, /hometown, an admin teleport) leaves the flag set. Called from
        /// OnTeleportComplete: if the player is wave-active but no longer in their bound run instance, forfeit the
        /// run so the persisted state stops applying. Because the arrival-arming callback (StartWaveChallenge) runs
        /// before the client's teleport-complete ack, a genuine arena arrival already has
        /// waveChallengeRunInstance == Location.Instance here and is left untouched; a flag left set by a
        /// StartWaveChallenge that no-opped (run instance still 0) is cleared silently.
        /// </summary>
        public void CheckWaveChallengeInstanceExit()
        {
            if (!WaveChallengeActive)
                return;

            // still inside the bound, started run - nothing to do
            if (waveChallengeRunInstance != 0 && Location != null && Location.Instance == waveChallengeRunInstance)
                return;

            // a started run whose instance we have now left - tell the player they gave it up
            if (waveChallengeRunInstance != 0)
                WaveChallengeMsg("You have abandoned the gauntlet.");

            ForfeitWaveChallenge();
        }

        /// <summary>
        /// Forfeit: ends an in-progress wave run, keeping the waves already cleared as the score. Used when a
        /// teleport moves the player out of the arena by any means other than death or the exit portal. A mid-run
        /// logout is handled instead by the login clear in WorldManager (mirrors DPS and Defense).
        /// </summary>
        public void ForfeitWaveChallenge()
        {
            if (!WaveChallengeActive)
                return;

            if (waveChallengeRunInstance != 0)
            {
                FinishWaveChallengeRun(null, teleportOut: false);
                return;
            }

            // armed but never started (StartWaveChallenge no-opped) - just drop the flag
            WaveChallengeActive = false;
            RushNextPlayerSave(5);
        }
    }
}
