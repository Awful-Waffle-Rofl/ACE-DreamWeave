using System;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    // WaffleACE DPS challenge: a portal (PropertyInt.DpsChallengeDuration > 0) drops the player into a
    // strictly single-player ephemeral arena holding one passive target dummy (a Creature weenie flagged
    // with PropertyBool.DpsChallengeTarget). On arrival the run is armed: the player gets N seconds to
    // deal as much damage as possible; at expiry the server reads the dummy's DamageHistory, sums the
    // damage that resolves to this player (pets included), records a personal best, and teleports them
    // back out. Leaving the instance early, dying, or logging out mid-run forfeits the run with no score.
    partial class Player
    {
        // Instance id the current run is bound to; 0 = no active run. The staged ActionChain segments and
        // the expiry handler all guard on this (plus the player's live Location.Instance) so a run that was
        // abandoned - the player left the instance, died out of it, or logged out - simply no-ops.
        private uint dpsChallengeRunInstance;

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
        /// Starts the timed DPS-challenge run. Called on arrival in the arena instance (from Portal.ActOnUse's
        /// teleport-completion follow-up), so the countdown effectively begins when the player lands. Binds the
        /// run to the player's current instance and schedules staged warnings and the expiry via one ActionChain.
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
            DpsChallengeActive = true;
            RushNextPlayerSave(5);

            DpsChallengeMsg($"The trial begins! You have {durationSeconds:N0} seconds to deal as much damage as possible.");

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

            if (durationSeconds > 30)
                Stage(durationSeconds - 30, () => { if (DpsChallengeRunValid(runInstance)) DpsChallengeMsg("30 seconds remaining!"); });

            if (durationSeconds > 10)
                Stage(durationSeconds - 10, () => { if (DpsChallengeRunValid(runInstance)) DpsChallengeMsg("10 seconds remaining!"); });

            // final per-second countdown (5, 4, 3, 2, 1). Each tick is only scheduled if it fits within
            // the run, so short/configurable durations degrade gracefully - no negative delays, and no
            // ticks doubling up at t=0 with an earlier warning.
            for (var secondsLeft = 5; secondsLeft >= 1; secondsLeft--)
            {
                var tick = secondsLeft;
                if (durationSeconds > tick)
                    Stage(durationSeconds - tick, () => { if (DpsChallengeRunValid(runInstance)) DpsChallengeMsg($"{tick}..."); });
            }

            Stage(durationSeconds, () => FinishDpsChallenge(runInstance, durationSeconds));

            chain.EnqueueChain();
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

            // the arena dummy: first creature in this landblock flagged DpsChallengeTarget
            var dummy = CurrentLandblock?.GetAllWorldObjectsForDiagnostics()
                .OfType<Creature>()
                .FirstOrDefault(c => c.GetProperty(PropertyBool.DpsChallengeTarget) == true);

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
            var currentServerMax = PlayerManager.GetAllPlayers()
                .Select(p => p.GetProperty(PropertyInt64.BestDpsScore) ?? 0)
                .DefaultIfEmpty(0)
                .Max();

            if (score > 0 && score > currentServerMax)
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
