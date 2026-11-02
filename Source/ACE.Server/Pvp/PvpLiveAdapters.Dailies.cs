using System;

using log4net;

using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;

namespace ACE.Server.Pvp
{
    internal sealed partial class LivePvpPlayerGateway
    {
        /// <summary>One forfeit dispatcher for the process (see PvpDailyForfeitDispatcher for the offline path).</summary>
        private static readonly PvpDailyForfeitDispatcher forfeitDispatcher = new PvpDailyForfeitDispatcher(new LivePvpDailyForfeitTargets());

        /// <summary>
        /// PvP dailies: queues <see cref="WorldObjects.Player.ApplyPvpDailyFacts"/> on the player's own action chain, once. An ordinary facts
        /// record for a player who is offline or already logging out (a queue that may never run again) is skipped and logged: unlike Blood,
        /// nothing is owed, and a missed match only means the daily needs one more match. A FORFEIT fact is the exception: it goes to
        /// <see cref="PvpDailyForfeitDispatcher"/>, which pends a GladiatorsStreak reset on the stored character for the next login.
        /// </summary>
        public void ApplyDailyFacts(PvpMatchFacts facts)
        {
            if (facts == null)
                return;

            // A Live 1v1 forfeit fact must reach a forfeiter who has logged out (the usual logout forfeit): it has its own offline path.
            if (facts.Forfeited)
            {
                forfeitDispatcher.Dispatch(facts);
                return;
            }

            var p = Online(facts.CharacterId);

            if (p == null || p.IsLoggingOut)
            {
                log.Info($"[PVP] 0x{facts.CharacterId:X8} is offline at the end of match {facts.MatchId}; no daily progress from it");
                return;
            }

            p.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    p.ApplyPvpDailyFacts(facts);
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] {p.Name} (0x{p.Guid.Full:X8}): applying the daily facts of match {facts.MatchId} threw", ex);
                }
            }));
        }
    }

    /// <summary>The live <see cref="IPvpDailyForfeitTargets"/>: PlayerManager, the player's action queue, OfflinePlayer and WorldManager.ActionQueue.</summary>
    internal sealed class LivePvpDailyForfeitTargets : IPvpDailyForfeitTargets
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(LivePvpDailyForfeitTargets));

        public bool TryApplyOnline(PvpMatchFacts facts)
        {
            var p = PlayerManager.GetOnlinePlayer(facts.CharacterId);

            // Logging out counts as offline: its queue may never run again.
            if (p == null || p.IsLoggingOut)
                return false;

            p.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    p.ApplyPvpDailyFacts(facts);
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] {p.Name} (0x{p.Guid.Full:X8}): applying the forfeit fact of match {facts.MatchId} threw", ex);
                }
            }));

            return true;
        }

        public bool TryPendOffline(uint characterId)
        {
            // The Blood grant's offline pattern (Player.GrantArenaBloodOffline): OfflinePlayer takes its own biota lock per property.
            var offline = PlayerManager.GetOfflinePlayer(characterId);

            if (offline == null)
                return false;

            offline.SetProperty(PropertyString.PvpDailyGladiatorsStreakState, PvpDailyProgress.PendingStreakResetMarker);
            offline.SaveBiotaToDatabase();
            return true;
        }

        public void ScheduleOnWorld(double seconds, Action action)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(seconds);
            chain.AddAction(WorldManager.ActionQueue, () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    log.Error("[PVP] the GladiatorsStreak forfeit reset retry threw", ex);
                }
            });
            chain.EnqueueChain();
        }
    }
}
