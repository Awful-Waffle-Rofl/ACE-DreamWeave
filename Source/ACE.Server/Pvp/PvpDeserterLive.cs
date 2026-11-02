using System;

using log4net;

using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;

namespace ACE.Server.Pvp
{
    /// <summary>The live gateway's conduct half (Docs/Pvp/BATTLEGROUNDS.md "Deserter lockout"): one process-wide dispatcher stores every record.</summary>
    internal sealed partial class LivePvpPlayerGateway : IPvpConductGateway
    {
        private static readonly PvpDeserterRecordDispatcher deserterDispatcher = new PvpDeserterRecordDispatcher(new LivePvpDeserterWriteTargets());

        public void PersistDeserterRecord(uint characterId, PvpDeserterRecord record)
        {
            try
            {
                deserterDispatcher.Persist(characterId, record);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] 0x{characterId:X8}: queuing the deserter record threw", ex);
            }
        }
    }

    /// <summary>The live <see cref="IPvpDeserterWriteTargets"/>: PlayerManager, the player's action queue, and WorldManager.ActionQueue.</summary>
    internal sealed class LivePvpDeserterWriteTargets : IPvpDeserterWriteTargets
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(LivePvpDeserterWriteTargets));

        public bool IsOnline(uint characterId) => PlayerManager.GetOnlinePlayer(characterId) != null;

        public bool IsLoggingOut(uint characterId) => PlayerManager.GetOnlinePlayer(characterId)?.IsLoggingOut ?? false;

        public bool TryEnqueueOnPlayer(uint characterId, Action action)
        {
            var p = PlayerManager.GetOnlinePlayer(characterId);

            // A logging-out player's queue may never run again: the world-thread fallback stores the record once the offline record exists.
            if (p == null || p.IsLoggingOut)
                return false;

            p.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] {p.Name} (0x{p.Guid.Full:X8}): the queued deserter record write threw", ex);
                }
            }));

            return true;
        }

        public bool WriteOnline(uint characterId, PvpDeserterRecord record)
        {
            var p = PlayerManager.GetOnlinePlayer(characterId);

            if (p == null || p.IsLoggingOut)
                return false;

            p.SetProperty(PropertyInt.PvpDeserterStrikes, record.Strikes);
            p.SetProperty(PropertyInt64.PvpDeserterLastStrikeAt, record.LastStrikeAt);
            p.SetProperty(PropertyInt64.PvpDeserterLockoutUntil, record.LockoutUntil);
            p.RushNextPlayerSave(5);

            log.Info($"[PVP] {p.Name} (0x{characterId:X8}): deserter record stored (strikes {record.Strikes}, last strike {record.LastStrikeAt}, locked until {record.LockoutUntil})");
            return true;
        }

        public bool WriteOffline(uint characterId, PvpDeserterRecord record)
        {
            var offline = PlayerManager.GetOfflinePlayer(characterId);

            if (offline == null)
                return false;

            offline.SetProperty(PropertyInt.PvpDeserterStrikes, record.Strikes);
            offline.SetProperty(PropertyInt64.PvpDeserterLastStrikeAt, record.LastStrikeAt);
            offline.SetProperty(PropertyInt64.PvpDeserterLockoutUntil, record.LockoutUntil);
            offline.SaveBiotaToDatabase();

            log.Info($"[PVP] 0x{characterId:X8} (offline): deserter record stored (strikes {record.Strikes}, last strike {record.LastStrikeAt}, locked until {record.LockoutUntil})");
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
                    log.Error("[PVP] the deserter record fallback threw", ex);
                }
            });
            chain.EnqueueChain();
        }
    }
}
