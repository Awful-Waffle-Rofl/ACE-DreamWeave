using System;
using ACE.Server.Managers;
using log4net;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Source for the 'fast_tick_all_players' server property consumed by Player.FastTick.
    /// Lives outside Player because Player's static constructor touches the world database,
    /// which makes Player statics unreachable from ACE.Server.Tests. A swappable seam because
    /// PropertyManager reads throw there (no shard config), and FastTick sits on the per-tick
    /// path so it must never throw.
    /// </summary>
    public static class FastTickPolicy
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static Func<bool> AllPlayers = () =>
        {
            try
            {
                return PropertyManager.GetBool("fast_tick_all_players").Item;
            }
            catch (Exception ex)
            {
                log.Error("could not read fast_tick_all_players; treating it as off", ex);
                return false;
            }
        };
    }
}
