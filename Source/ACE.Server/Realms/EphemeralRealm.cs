using System;
using System.Collections.Generic;

using ACE.Server.WorldObjects;

namespace ACE.Server.Realms
{
    /// <summary>
    /// ACRealms port (rulesets stubbed): a temporary, on-demand copy of a single
    /// landblock - an 'instance' in the traditional MMO sense. Created via
    /// RealmManager.GetNewEphemeralLandblock; lives until it empties out and the
    /// landblock unloads. Nothing inside is persisted.
    /// </summary>
    public class EphemeralRealm
    {
        public Player Owner { get; }
        public List<Player> AllowedPlayers { get; } = new List<Player>();
        public bool OpenToFellowship { get; set; } = true;
        public DateTime ExpiresAt { get; } = DateTime.UtcNow.AddDays(1);

        public EphemeralRealm(Player owner)
        {
            Owner = owner;
        }

        /// <summary>
        /// True if this player may enter the instance
        /// </summary>
        public bool Accepts(Player player)
        {
            if (DateTime.UtcNow > ExpiresAt)
                return false;

            if (player == Owner || AllowedPlayers.Contains(player))
                return true;

            if (OpenToFellowship && Owner?.Fellowship != null && player.Fellowship == Owner.Fellowship)
                return true;

            return false;
        }
    }
}
