using System;

using log4net;

namespace ACE.Server.Pvp
{
    /// <summary>What <see cref="PvpDailyForfeitDispatcher"/> needs from the live server. The live one is in PvpLiveAdapters.Dailies.cs.</summary>
    public interface IPvpDailyForfeitTargets
    {
        /// <summary>
        /// Queues the forfeit fact on the character's own action chain. False, with nothing queued, when the character is offline OR
        /// logging out (a logging-out queue may never run again): those take the offline path.
        /// </summary>
        bool TryApplyOnline(PvpMatchFacts facts);

        /// <summary>
        /// Writes <see cref="PvpDailyProgress.PendingStreakResetMarker"/> into the stored (offline) character's streak state and saves it.
        /// False, with nothing written, when there is no offline record yet (still online, or mid-logout).
        /// </summary>
        bool TryPendOffline(uint characterId);

        /// <summary>Runs <paramref name="action"/> on the world thread after <paramref name="seconds"/>.</summary>
        void ScheduleOnWorld(double seconds, Action action);
    }

    /// <summary>
    /// Delivers a Live 1v1 forfeiter's forfeit fact (PvpMatchFacts.ForfeitOf), which only resets GladiatorsStreak, whether or not the
    /// forfeiter is still online. A logout forfeit is the common case: the player is offline or logging out at Resolve. An offline
    /// character has no quest registry to write (OfflinePlayer exposes properties only), so the offline path stores a PENDING RESET in the
    /// streak state property and Player.ApplyPendingPvpStreakReset applies it at the next login. Attempts, in order, each time:
    /// online and not logging out: queue on the player; else an offline record exists: pend the reset; else (mid-logout) retry on the world
    /// thread after <see cref="RetrySeconds"/>, up to <see cref="MaxAttempts"/> times. Each attempt takes exactly one path, so the fact is
    /// applied at most once.
    /// </summary>
    public sealed class PvpDailyForfeitDispatcher
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpDailyForfeitDispatcher));

        public const double RetrySeconds = 30;

        public const int MaxAttempts = 10;

        private readonly IPvpDailyForfeitTargets targets;

        public PvpDailyForfeitDispatcher(IPvpDailyForfeitTargets targets)
        {
            this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
        }

        /// <summary>World thread. Starts delivery of one forfeit fact. Anything that is not a forfeit fact is ignored.</summary>
        public void Dispatch(PvpMatchFacts facts)
        {
            if (facts == null || !facts.Forfeited)
                return;

            Attempt(facts, 1);
        }

        private void Attempt(PvpMatchFacts facts, int attempt)
        {
            if (targets.TryApplyOnline(facts))
                return;

            if (targets.TryPendOffline(facts.CharacterId))
            {
                log.Info($"[PVP] 0x{facts.CharacterId:X8} forfeited match {facts.MatchId} while offline or logging out; GladiatorsStreak reset pending for their next login");
                return;
            }

            if (attempt >= MaxAttempts)
            {
                log.Error($"[PVP] 0x{facts.CharacterId:X8}: the GladiatorsStreak reset for forfeiting match {facts.MatchId} was NOT applied after {attempt} attempts (no online player and no offline record)");
                return;
            }

            log.Info($"[PVP] 0x{facts.CharacterId:X8}: no online player and no offline record for the GladiatorsStreak reset of forfeiting match {facts.MatchId}; attempt {attempt} of {MaxAttempts} failed, retrying in {RetrySeconds} s");
            targets.ScheduleOnWorld(RetrySeconds, () => Attempt(facts, attempt + 1));
        }
    }
}
