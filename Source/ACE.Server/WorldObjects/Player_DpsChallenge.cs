using System;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    // WaffleACE DPS challenge: a portal (PropertyInt.DpsChallengeDuration > 0) drops the player into a
    // strictly single-player ephemeral arena holding one passive target dummy (a Creature weenie flagged
    // with PropertyBool.DpsChallengeTarget). On arrival the run is armed and a short PREP WINDOW runs
    // (DpsChallengePrepSeconds): the dummy is held out of the world entirely, so the player has time to
    // finish loading in, buff and position before anything can be hit. When the prep window ends the dummy
    // spawns and the timed trial starts: the player gets N seconds to deal as much damage as possible; at
    // expiry the server reads the dummy's DamageHistory, sums the damage that resolves to this player (pets
    // included), records a personal best, and teleports them back out. Leaving the instance early, dying, or
    // logging out mid-run forfeits the run with no score.
    //
    // Why the dummy is REMOVED for the prep window rather than merely ignored: scoring reads the dummy's
    // DamageHistory, which has no notion of when the trial started. A dummy standing in the arena during the
    // prep window could be beaten on for the whole window and every point of it would land in the score.
    // Holding it out of the world is what keeps the prep window free.
    partial class Player
    {
        /// <summary>
        /// Seconds between arrival in the arena and the target dummy spawning / the clock starting. This is the
        /// player's prep window - finish loading in, buff, position - and nothing is scorable during it.
        /// </summary>
        private const double DpsChallengePrepSeconds = 10;

        /// <summary>
        /// How often the prep-window hold re-checks whether the arena has finished populating. The arena's static
        /// dummy is placed by the landblock's own load, which races the arrival teleport, so the hold cannot
        /// simply look once.
        /// </summary>
        private const double DpsChallengeHoldPollSeconds = 0.25;

        /// <summary>
        /// How many times the hold re-checks before giving up (0.25s * 32 = 8s, comfortably inside the 10s prep
        /// window and leaving room for the spawn that follows it).
        /// </summary>
        private const int DpsChallengeHoldAttempts = 32;

        // Instance id the current run is bound to; 0 = no active run. The staged ActionChain segments and
        // the expiry handler all guard on this (plus the player's live Location.Instance) so a run that was
        // abandoned - the player left the instance, died out of it, or logged out - simply no-ops.
        private uint dpsChallengeRunInstance;

        // What the arena's target dummy was and where it stood, captured off the arena's own static placement
        // when the run is armed and used to put a FRESH one back at the end of the prep window. A zero wcid
        // means the static dummy was never found - see SpawnDpsChallengeTarget.
        private Position dpsChallengeTargetSpawn;
        private uint dpsChallengeTargetWcid;

        /// <summary>
        /// Best recorded total damage across the player's DPS-challenge runs. Persisted on the character biota.
        /// </summary>
        public long BestDpsScore
        {
            get => GetProperty(PropertyInt64.BestDpsScore) ?? 0;
            set => SetProperty(PropertyInt64.BestDpsScore, value);
        }

        /// <summary>
        /// True while a DPS-challenge run is armed/in progress. Persisted so a mid-run logout can be caught at
        /// next login (see WorldManager.DoPlayerEnterWorld) and the player re-homed to their lifestone.
        /// </summary>
        public bool DpsChallengeActive
        {
            get => GetProperty(PropertyBool.DpsChallengeActive) ?? false;
            set => SetProperty(PropertyBool.DpsChallengeActive, value);
        }

        private void DpsChallengeMsg(string message)
        {
            Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.System));
        }

        /// <summary>
        /// Reconciles the persisted run flag against the player's current instance, the DPS mirror of
        /// <see cref="CheckSurvivalChallengeInstanceExit"/>. Only <see cref="FinishDpsChallenge"/> and the
        /// login clear in WorldManager.DoPlayerEnterWorld reset <see cref="DpsChallengeActive"/>, so any
        /// other way of ending up outside the arena leaves the flag set: if the player is DPS-active but no
        /// longer standing in their bound run instance, the run is forfeit and the persisted flag cleared.
        ///
        /// <para/>
        /// A flag left set by an arming that never got its <see cref="StartDpsChallenge"/> - the run
        /// instance is still 0, which is the state a REFUSED entry leaves behind (Portal.ActOnUse persists
        /// the flag ahead of the teleport, and Player.Teleport can refuse a dead ephemeral destination after
        /// that) - is cleared silently, with no "abandoned" message, because from the player's point of view
        /// no trial ever began.
        ///
        /// <para/>
        /// Called from Player.Teleport's ephemeral-refusal path. Deliberately NOT called from
        /// OnTeleportComplete: the three existing reconcilers are wired there and DPS is not, which is a
        /// pre-existing gap that predates this method and is left alone rather than widened here.
        /// </summary>
        public void CheckDpsChallengeInstanceExit()
        {
            if (!DpsChallengeActive)
                return;

            // still inside the bound, started run - nothing to do. This covers the prep window too: the run is
            // bound the moment the player lands, before the dummy spawns.
            if (dpsChallengeRunInstance != 0 && Location != null && Location.Instance == dpsChallengeRunInstance)
                return;

            // a started run whose instance we have now left - tell the player they gave it up
            if (dpsChallengeRunInstance != 0)
                DpsChallengeMsg("You have abandoned the trial.");

            ForfeitDpsChallenge();
        }

        /// <summary>
        /// Ends an in-progress DPS run with no score. The staged ActionChain segments already self-terminate
        /// via <see cref="DpsChallengeRunValid"/>; this additionally clears the persisted active flag so a
        /// dangling bool does not survive until the next login clear.
        /// </summary>
        public void ForfeitDpsChallenge()
        {
            if (!DpsChallengeActive)
                return;

            dpsChallengeRunInstance = 0;
            DpsChallengeActive = false;
            RushNextPlayerSave(5);
        }

        /// <summary>
        /// True if the run bound to <paramref name="runInstance"/> is still valid for this player: the run must
        /// be armed for that exact instance and the player must still be standing in it.
        /// </summary>
        private bool DpsChallengeRunValid(uint runInstance)
        {
            return runInstance != 0
                && dpsChallengeRunInstance == runInstance
                && CurrentLandblock != null
                && Location != null
                && Location.Instance == runInstance;
        }

        /// <summary>
        /// The arena dummy: the first creature in this landblock flagged DpsChallengeTarget, or null.
        /// </summary>
        private Creature FindDpsChallengeTarget()
        {
            return CurrentLandblock?.GetAllWorldObjectsForDiagnostics()
                .OfType<Creature>()
                .FirstOrDefault(c => c.GetProperty(PropertyBool.DpsChallengeTarget) == true);
        }

        /// <summary>
        /// Starts the timed DPS-challenge run. Called on arrival in the arena instance (from Portal.ActOnUse's
        /// teleport-completion follow-up). Binds the run to the player's current instance, holds the target
        /// dummy out of the world for the prep window, then spawns it and starts the clock.
        /// </summary>
        public void StartDpsChallenge(double durationSeconds)
        {
            // Mule (WaffleACE): a mule cannot fight, so it has no business in a Proving Grounds run.
            if (MuleBlocked(MuleAction.StartChallenge))
                return;

            if (Location == null || durationSeconds <= 0)
                return;

            var runInstance = Location.Instance;
            dpsChallengeRunInstance = runInstance;
            dpsChallengeTargetSpawn = null;
            dpsChallengeTargetWcid = 0;
            DpsChallengeActive = true;
            RushNextPlayerSave(5);

            // pull the dummy out of the arena for the duration of the prep window
            HoldDpsChallengeTarget(runInstance, DpsChallengeHoldAttempts);

            DpsChallengeMsg($"Steel yourself. The target arrives in {DpsChallengePrepSeconds:N0} seconds, and you will then have {durationSeconds:N0} seconds to deal as much damage as possible.");

            // One chain covers the whole run, staged at absolute times measured from arrival: the prep
            // countdown, the spawn + start of the clock at t = prep, then the run's own warnings and expiry.
            var chain = new ActionChain();
            double elapsed = 0;

            // Adds a segment that fires at absolute time 'at' seconds into the run, guarded so it no-ops on forfeit.
            void Stage(double at, Action action)
            {
                var delta = at - elapsed;
                if (delta < 0)
                    delta = 0;
                chain.AddDelaySeconds(delta);
                elapsed = at;
                chain.AddAction(this, action);
            }

            // prep countdown (5, 4, 3, 2, 1), each tick only scheduled if it fits inside the prep window
            for (var secondsLeft = 5; secondsLeft >= 1; secondsLeft--)
            {
                var tick = secondsLeft;
                if (DpsChallengePrepSeconds > tick)
                    Stage(DpsChallengePrepSeconds - tick, () => { if (DpsChallengeRunValid(runInstance)) DpsChallengeMsg($"{tick}..."); });
            }

            // t = prep: the dummy spawns and the clock starts
            Stage(DpsChallengePrepSeconds, () =>
            {
                if (!DpsChallengeRunValid(runInstance))
                    return;

                if (!SpawnDpsChallengeTarget(runInstance))
                {
                    AbortDpsChallenge("The trial cannot begin - the target never appeared. You are being returned.");
                    return;
                }

                DpsChallengeMsg($"The trial begins! You have {durationSeconds:N0} seconds to deal as much damage as possible.");
            });

            var runEnd = DpsChallengePrepSeconds + durationSeconds;

            if (durationSeconds > 30)
                Stage(runEnd - 30, () => { if (DpsChallengeRunValid(runInstance)) DpsChallengeMsg("30 seconds remaining!"); });

            if (durationSeconds > 10)
                Stage(runEnd - 10, () => { if (DpsChallengeRunValid(runInstance)) DpsChallengeMsg("10 seconds remaining!"); });

            // final per-second countdown (5, 4, 3, 2, 1). Each tick is only scheduled if it fits within
            // the run, so short/configurable durations degrade gracefully - no negative delays, and no
            // ticks doubling up with an earlier warning.
            for (var secondsLeft = 5; secondsLeft >= 1; secondsLeft--)
            {
                var tick = secondsLeft;
                if (durationSeconds > tick)
                    Stage(runEnd - tick, () => { if (DpsChallengeRunValid(runInstance)) DpsChallengeMsg($"{tick}..."); });
            }

            Stage(runEnd, () => FinishDpsChallenge(runInstance, durationSeconds));

            chain.EnqueueChain();
        }

        /// <summary>
        /// Removes the arena's static target dummy for the prep window, remembering what it was and where it
        /// stood so an identical one can be spawned when the window ends.
        ///
        /// The arena's statics are placed by the landblock's own load, which RACES the arrival teleport, so a
        /// single look on arrival can legitimately find nothing. This re-checks on a short poll until the
        /// landblock reports CreateWorldObjectsCompleted, at which point the dummy is either there or the arena
        /// content is broken.
        /// </summary>
        private void HoldDpsChallengeTarget(uint runInstance, int attemptsLeft)
        {
            if (!DpsChallengeRunValid(runInstance))
                return;

            if (CurrentLandblock.CreateWorldObjectsCompleted)
            {
                var dummy = FindDpsChallengeTarget();

                if (dummy == null)
                {
                    log.Error($"[DPS] {Name} (0x{Guid}) - arena landblock 0x{Location.LandblockId.Landblock:X4} finished loading with no PropertyBool.DpsChallengeTarget creature in it, so the trial has nothing to spawn. Check the arena's realm placement.");
                    return;
                }

                dpsChallengeTargetWcid = dummy.WeenieClassId;
                dpsChallengeTargetSpawn = new Position(dummy.Location);
                dummy.Destroy();
                return;
            }

            if (attemptsLeft <= 0)
            {
                log.Error($"[DPS] {Name} (0x{Guid}) - arena landblock 0x{Location.LandblockId.Landblock:X4} had not finished loading after {DpsChallengeHoldAttempts * DpsChallengeHoldPollSeconds:N1}s, so the target dummy could not be held out of the prep window.");
                return;
            }

            var chain = new ActionChain();
            chain.AddDelaySeconds(DpsChallengeHoldPollSeconds);
            chain.AddAction(this, () => HoldDpsChallengeTarget(runInstance, attemptsLeft - 1));
            chain.EnqueueChain();
        }

        /// <summary>
        /// Puts a fresh target dummy into the arena at the end of the prep window. Returns false if nothing made
        /// it into the world, in which case the caller aborts the run rather than timing the player against an
        /// empty room.
        /// </summary>
        private bool SpawnDpsChallengeTarget(uint runInstance)
        {
            if (dpsChallengeTargetWcid == 0 || dpsChallengeTargetSpawn == null)
            {
                log.Error($"[DPS] {Name} (0x{Guid}) - no target-dummy placement was captured during the prep window, so the trial cannot be started.");
                return false;
            }

            // Defensive: a dummy that populated after the hold gave up would still be carrying whatever damage
            // the prep window put on it, and FindDpsChallengeTarget takes the FIRST match - so a leftover could
            // be scored in place of the fresh one. Clear the room first.
            var stale = FindDpsChallengeTarget();

            if (stale != null)
            {
                log.Warn($"[DPS] {Name} (0x{Guid}) - a target dummy was still in the arena at the end of the prep window; destroying it before spawning the scored one.");
                stale.Destroy();
            }

            var wo = WorldObjectFactory.CreateNewWorldObject(dpsChallengeTargetWcid);

            if (wo == null)
            {
                log.Error($"[DPS] {Name} (0x{Guid}) - failed to create target dummy wcid {dpsChallengeTargetWcid}.");
                return false;
            }

            // only a Creature carries a DamageHistory, which is the entire basis of the score
            if (!(wo is Creature))
            {
                log.Error($"[DPS] {Name} (0x{Guid}) - target dummy wcid {dpsChallengeTargetWcid} is not a Creature, so it could never be scored; dropped.");
                wo.Destroy();
                return false;
            }

            wo.Location = new Position(dpsChallengeTargetSpawn, runInstance);

            // a placement pointing outside the arena would drag the dummy out of the private instance
            if (wo.Location.InstancedLandblock != Location.InstancedLandblock)
            {
                log.Error($"[DPS] {Name} (0x{Guid}) - target dummy wcid {dpsChallengeTargetWcid} resolved to landblock 0x{wo.Location.LandblockId.Landblock:X4}, which is not the arena; dropped.");
                wo.Destroy();
                return false;
            }

            if (!wo.EnterWorld())
            {
                log.Error($"[DPS] {Name} (0x{Guid}) - target dummy wcid {dpsChallengeTargetWcid} failed to enter the world at {wo.Location.ToLOCString()}.");
                wo.Destroy();
                return false;
            }

            // OPT OUT OF DECAY, for the same reason the wave arena does (see Player_WaveChallenge.SpawnWave): a
            // hand-spawned creature gets a DYNAMIC guid and has no Generator back-reference, so it is decayable
            // and the landblock heartbeat would rot it out from under a long run. -1 means never rot; the arena
            // instance is torn down with the run either way.
            wo.TimeToRot = -1;

            return true;
        }

        /// <summary>
        /// Ends a run that could never be scored (the target failed to spawn). Clears the run without touching
        /// the personal best or the server record, and returns the player the same way a finished run does.
        /// </summary>
        private void AbortDpsChallenge(string message)
        {
            DpsChallengeMsg(message);

            var exitTo = GetPosition(PositionType.EphemeralRealmExitTo);
            var dest = exitTo != null ? new Position(exitTo) : Sanctuary;

            SetPosition(PositionType.EphemeralRealmExitTo, null);

            // clears the bound run - which is also what makes every remaining staged segment no-op - and the
            // persisted active flag
            ForfeitDpsChallenge();

            if (dest != null)
            {
                var exitChain = new ActionChain();
                exitChain.AddDelaySeconds(3);
                exitChain.AddAction(this, () => Teleport(dest));
                exitChain.EnqueueChain();
            }
        }

        /// <summary>
        /// Expiry handler: scores the run from the dummy's DamageHistory, records a personal best, and teleports
        /// the player back out. No-ops (forfeits) if the run is no longer valid.
        /// </summary>
        private void FinishDpsChallenge(uint runInstance, double durationSeconds)
        {
            if (!DpsChallengeRunValid(runInstance))
                return;

            // consume the run so nothing can re-trigger scoring for it
            dpsChallengeRunInstance = 0;

            // the arena dummy: the one spawned at the end of the prep window, so its DamageHistory only ever
            // covers the scored window
            var dummy = FindDpsChallengeTarget();

            double total = 0;
            if (dummy?.DamageHistory != null)
            {
                foreach (var info in dummy.DamageHistory.TotalDamage.Values)
                {
                    // resolve pet damage back to the owning player, then keep only what this player dealt
                    var source = info.TryGetPetOwnerOrAttacker();
                    if (source != null && source.Guid == Guid)
                        total += info.TotalDamage;
                }
            }

            var score = (long)total;
            var dps = durationSeconds > 0 ? score / durationSeconds : 0;

            DpsChallengeMsg($"Time! You dealt {score:N0} damage ({dps:N0} DPS).");

            // Server-record check: read the current max across ALL players BEFORE persisting this player's
            // new best (same online+offline scan the /top command uses). Doing it first is what lets the
            // reigning record-holder beat their own record - their stored best is still the old value here.
            // The very first score ever also lands here: currentServerMax is then 0, any positive score
            // exceeds it, and it IS the inaugural record, so it is announced too.
            // Staff (WaffleACE): exempt players are excluded from the record bar real players are measured
            // against, and don't trigger the world broadcast themselves - their own personal-best persist below
            // still runs unconditionally, only the visibility of it is affected.
            var exemptNames = LeaderboardExemptionManager.GetExemptAccountNames();
            var currentServerMax = PlayerManager.GetAllPlayers()
                .Where(p => !LeaderboardExemptionManager.IsExempt(p, exemptNames))
                .Select(p => p.GetProperty(PropertyInt64.BestDpsScore) ?? 0)
                .DefaultIfEmpty(0)
                .Max();

            if (score > 0 && score > currentServerMax && !LeaderboardExemptionManager.IsExempt(this, exemptNames))
            {
                PlayerManager.BroadcastToAll(new GameMessageSystemChat(
                    $"[The Proving Grounds] {Name} has set a new record: {score:N0} damage in {durationSeconds:N0} seconds!",
                    ChatMessageType.WorldBroadcast));
            }

            if (score > BestDpsScore)
            {
                BestDpsScore = score;
                DpsChallengeMsg($"New personal best: {score:N0} damage!");
            }

            // return the player to where they came from (fallback: their lifestone), clear the exit stamp and flag
            var exitTo = GetPosition(PositionType.EphemeralRealmExitTo);
            var dest = exitTo != null ? new Position(exitTo) : Sanctuary;

            SetPosition(PositionType.EphemeralRealmExitTo, null);
            DpsChallengeActive = false;
            RushNextPlayerSave(5);

            if (dest != null)
            {
                var exitChain = new ActionChain();
                exitChain.AddDelaySeconds(3);   // brief pause so the player can read the result
                exitChain.AddAction(this, () => Teleport(dest));   // Teleport self-validates the instance destination
                exitChain.EnqueueChain();
            }
        }
    }
}
