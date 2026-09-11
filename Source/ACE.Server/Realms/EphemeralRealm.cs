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

        /// <summary>
        /// Guards <see cref="allowedPlayers"/>. Admit() runs on whatever thread reached the caller (a gem
        /// used from the ground arrives on a landblock action chain, not the world thread) while Accepts()
        /// is read from the teleport path, so the add and the read have to agree on one lock.
        /// </summary>
        private readonly object allowedLock = new object();
        private readonly List<Player> allowedPlayers = new List<Player>();

        /// <summary>Snapshot of the players admitted on top of <see cref="Owner"/>. Add through <see cref="Admit"/>.</summary>
        public IReadOnlyList<Player> AllowedPlayers
        {
            get { lock (allowedLock) return allowedPlayers.ToArray(); }
        }

        public bool OpenToFellowship { get; set; } = true;
        public DateTime ExpiresAt { get; } = DateTime.UtcNow.AddDays(1);

        /// <summary>Threads: the run this instance hosts, or null for every other ephemeral instance.</summary>
        public ACE.Server.ThreadDungeons.ThreadDungeonRun Run { get; set; }

        public EphemeralRealm(Player owner)
        {
            Owner = owner;
        }

        /// <summary>
        /// Admits a player who is not (or is no longer) the <see cref="Owner"/> reference. Owner is compared
        /// by REFERENCE in Accepts, and every login builds a NEW Player object (WorldManager.cs:266), so the
        /// original owner stops matching the moment they relog; the caller that still knows they are the
        /// rightful owner (by guid) re-admits them here. Idempotent.
        /// </summary>
        public void Admit(Player player)
        {
            if (player == null)
                return;

            lock (allowedLock)
            {
                if (!allowedPlayers.Contains(player))
                    allowedPlayers.Add(player);
            }
        }

        /// <summary>
        /// True if this player may enter the instance
        /// </summary>
        public bool Accepts(Player player)
        {
            if (DateTime.UtcNow > ExpiresAt)
                return false;

            if (player == Owner)
                return true;

            lock (allowedLock)
            {
                if (allowedPlayers.Contains(player))
                    return true;
            }

            if (OpenToFellowship && Owner?.Fellowship != null && player.Fellowship == Owner.Fellowship)
                return true;

            return false;
        }
    }
}
