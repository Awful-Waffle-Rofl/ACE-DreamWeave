using System.Linq;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// Which landblocks host PvP match instances: every arena map (<see cref="ArenaMapCatalog.All"/>) and every
    /// battleground map (<see cref="BattlegroundMapCatalog.Landblocks"/>). One definition for the carriers that
    /// ask "is this player physically inside a PvP map right now" (the IP-limit confinement), so a new map family
    /// is added in one place. Pure.
    /// </summary>
    public static class PvpMatchLandblocks
    {
        /// <summary>True when the 16-bit landblock number is an arena or battleground map.</summary>
        public static bool IsPvpMapLandblock(uint landblockShort) =>
            ArenaMapCatalog.All.Any(m => m.LandblockId == landblockShort)
            || BattlegroundMapCatalog.Landblocks.Contains(landblockShort);
    }
}