using System;
using ACE.Server.Managers;
using log4net;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Decides Player.FastTick: PK/PKLite always, otherwise the player's own /fasttick choice (PropertyBool.FastTickOptIn)
    /// when set, otherwise the 'fast_tick_all_players' server property (the default for players who have not chosen).
    /// Lives outside Player because Player's static constructor touches the world database,
    /// which makes Player statics unreachable from ACE.Server.Tests. A swappable seam because
    /// PropertyManager reads throw there (no shard config), and FastTick sits on the per-tick
    /// path so it must never throw.
    /// </summary>
    public static class FastTickPolicy
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The FastTick rule as a pure function. PK/PKLite are always fast tick. Otherwise an explicit player
        /// choice wins in either direction, and the server default is consulted only when there is none.
        /// </summary>
        public static bool Resolve(bool isPk, bool? optIn, Func<bool> allPlayers)
        {
            if (isPk)
                return true;

            if (optIn.HasValue)
                return optIn.Value;

            return allPlayers();
        }

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
