using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using log4net;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// What <see cref="PvpBloodGrantDispatcher"/> needs from the live server. The live implementation is in
    /// PvpLiveAdapters.cs; tests use a scripted fake whose player queue can run late or never.
    /// </summary>
    public interface IPvpBloodGrantTargets
    {
        /// <summary>The character is online (a logging-out player still counts as online here).</summary>
        bool IsOnline(uint characterId);

        /// <summary>
        /// Queues <paramref name="action"/> on the character's own action chain. False, with nothing queued, when the
        /// character is offline or already logging out (its queue may never run again).
        /// </summary>
        bool TryEnqueueOnPlayer(uint characterId, Action action);

        /// <summary>
        /// Pays on the online character. Runs on that character's action chain. False when it is no longer the live,
        /// not-logging-out owner of the character, in which case nothing was paid or counted.
        /// </summary>
        bool GrantOnline(uint characterId, int amount, PvpBloodGrant grant);

        /// <summary>
        /// Applies the cap and banks the Blood as owed on the stored (offline) character. False when there is no
        /// offline record to write to (still online, or mid-logout), in which case nothing was paid or counted.
        /// </summary>
        bool GrantOffline(uint characterId, int amount, PvpBloodGrant grant);

        /// <summary>Runs <paramref name="action"/> on the world thread after <paramref name="seconds"/>.</summary>
        void ScheduleOnWorld(double seconds, Action action);
    }

    /// <summary>
    /// Settles every Blood grant exactly once, even when the player's own action queue never runs again (a player
    /// mid death-teleport has no landblock, so nothing ticks their queue, and a disconnect then finalizes the logout
    /// at once). Each grant is kept PENDING under (match id, character id) until one path CLAIMS it - an atomic
    /// TryRemove, so the player-queue delegate and the world-thread fallback can never both pay:
    ///   - offline at payout: claimed and paid on the stored character at once;
    ///   - online: queued on the player's chain, AND a fallback scheduled on the world thread after
    ///     <see cref="FallbackSeconds"/>. Whichever runs first claims and pays; the other finds nothing to claim;
    ///   - logging out at payout: nothing is queued on the dying queue; the fallback settles it once the offline
    ///     record exists.
    /// A claimed grant that cannot be paid where it landed (the player logged out under the delegate, or the fallback
    /// fired mid-logout) is re-pended and retried by a fresh fallback, up to <see cref="MaxAttempts"/> times.
    /// </summary>
    public sealed class PvpBloodGrantDispatcher
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpBloodGrantDispatcher));

        public const double FallbackSeconds = 30;

        public const int MaxAttempts = 10;

        private sealed record Pending(int Amount, PvpBloodGrant Grant, int Attempt);

        private readonly IPvpBloodGrantTargets targets;
        private readonly ConcurrentDictionary<(Guid MatchId, uint CharacterId), Pending> pending = new();

        public PvpBloodGrantDispatcher(IPvpBloodGrantTargets targets)
        {
            this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
        }

        /// <summary>Grants still waiting to be claimed. Diagnostics and tests.</summary>
        public int PendingCount => pending.Count;

        /// <summary>The attempt (generation) of the pending grant for this match and character, or 0 when none is pending. Diagnostics and tests.</summary>
        public int AttemptOf(Guid matchId, uint characterId) => pending.TryGetValue((matchId, characterId), out var p) ? p.Attempt : 0;

        /// <summary>
        /// World thread. Pends the grant and starts it down the online or offline path. Duplicate protection here only
        /// covers a grant that is still PENDING; idempotency across settled grants is the coordinator's per-match
        /// BloodPaid flag, not this dispatcher's.
        /// </summary>
        public void Grant(uint characterId, int amount, PvpBloodGrant grant)
        {
            if (amount <= 0 || grant == null)
                return;

            var key = (grant.MatchId, characterId);

            if (!pending.TryAdd(key, new Pending(amount, grant, 1)))
            {
                log.Warn($"[PVP] 0x{characterId:X8}: a Blood grant for match {grant.MatchId} is already pending; not granting again");
                return;
            }

            if (!targets.IsOnline(characterId))
            {
                if (TryClaim(key, out var claimed))
                    SettleOffline(key, claimed);

                return;
            }

            Dispatch(key, 1);
        }

        /// <summary>Queues the claim on the player (if that queue may still run) and always arms the world-thread fallback.</summary>
        private void Dispatch((Guid MatchId, uint CharacterId) key, int generation)
        {
            // The fallback is armed for THIS generation only: after a retry re-pends the grant as generation + 1 (with
            // its own fresh fallback), an older fallback still outstanding finds a newer generation and ignores it, so
            // two outstanding timers can never consume two attempts between them.
            targets.ScheduleOnWorld(FallbackSeconds, () => Fallback(key, generation));

            targets.TryEnqueueOnPlayer(key.CharacterId, () =>
            {
                if (!TryClaim(key, out var claimed))
                    return; // the fallback already settled it

                if (!targets.GrantOnline(key.CharacterId, claimed.Amount, claimed.Grant))
                    Retry(key, claimed, "the player was no longer online when their queue ran");
            });
        }

        private void Fallback((Guid MatchId, uint CharacterId) key, int generation)
        {
            if (!TryClaimGeneration(key, generation, out var claimed))
                return; // already settled by the player's queue, or re-pended under a newer generation with its own fallback

            log.Warn($"[PVP] 0x{key.CharacterId:X8}: the Blood grant for match {key.MatchId} was not claimed by the player's queue within {FallbackSeconds} s; settling it on the world thread (attempt {claimed.Attempt})");
            SettleOffline(key, claimed);
        }

        private void SettleOffline((Guid MatchId, uint CharacterId) key, Pending claimed)
        {
            if (!targets.GrantOffline(key.CharacterId, claimed.Amount, claimed.Grant))
                Retry(key, claimed, "no offline record to write to yet");
        }

        private void Retry((Guid MatchId, uint CharacterId) key, Pending claimed, string why)
        {
            if (claimed.Attempt >= MaxAttempts)
            {
                log.Error($"[PVP] 0x{key.CharacterId:X8}: {claimed.Amount} Blood for match {key.MatchId} NOT paid after {claimed.Attempt} attempts ({why})");
                return;
            }

            var next = claimed with { Attempt = claimed.Attempt + 1 };

            if (!pending.TryAdd(key, next))
                return;

            Dispatch(key, next.Attempt);
        }

        /// <summary>The one atomic claim: exactly one caller gets the grant out.</summary>
        private bool TryClaim((Guid MatchId, uint CharacterId) key, out Pending claimed) => pending.TryRemove(key, out claimed);

        /// <summary>
        /// The fallback's claim: removes the entry only if it is still the generation the fallback was armed for. The
        /// value-conditional TryRemove is atomic, so it races safely with the player-queue claim and with a re-pend.
        /// </summary>
        private bool TryClaimGeneration((Guid MatchId, uint CharacterId) key, int generation, out Pending claimed)
        {
            claimed = null;

            if (!pending.TryGetValue(key, out var current) || current.Attempt != generation)
                return false;

            if (!pending.TryRemove(new KeyValuePair<(Guid MatchId, uint CharacterId), Pending>(key, current)))
                return false;

            claimed = current;
            return true;
        }
    }
}
