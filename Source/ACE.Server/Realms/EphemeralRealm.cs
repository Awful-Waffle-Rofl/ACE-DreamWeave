using System;
using System.Collections.Generic;

using ACE.Server.WorldObjects;

namespace ACE.Server.Realms
{
    /// <summary>
    /// ACRealms port (rulesets stubbed): a temporary, on-demand copy of a single
    /// landblock - an 'instance' in the traditional MMO sense - or, with a
    /// <see cref="Footprint"/>, of a group of outdoor landblocks. Created via
    /// RealmManager.GetNewEphemeralLandblock; lives until it empties out and its
    /// primary landblock unloads. Nothing inside is persisted.
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

        /// <summary>
        /// A copy that loads exactly the content a Thread run's copy loads (ThreadDungeonContentFilter with this exit
        /// portal wcid, no shard statics, no encounters) without a run behind it: puzzlegate-sweep's throwaway copy.
        /// Null for every other instance. Never set together with <see cref="Run"/>.
        /// </summary>
        public uint? RunlessThreadCopyExitPortalWcid { get; set; }

        /// <summary>The exit portal wcid the Thread content filter uses for this copy, or null when it is not a Thread copy.</summary>
        public uint? ThreadCopyExitPortalWcid => Run?.ExitPortalWcid ?? RunlessThreadCopyExitPortalWcid;

        /// <summary>
        /// A non-Thread keeper of this copy: a Pinnacle fight (PinnacleFightManager.AttachHold sets it when the copy is
        /// created and clears it to null when the fight ends). Never set together with <see cref="Run"/>.
        /// </summary>
        public IEphemeralHold Hold { get; set; }

        /// <summary>
        /// The idle window the landblock heartbeat should use for this copy instead of
        /// Landblock.UnloadInterval, or null for the default. A Thread run supplies one (its empty grace while it is
        /// Starting or Active, or a Cleared group run's loot hold while loot is still owed; see
        /// ThreadDungeonRun.UnloadIntervalOverride); otherwise a <see cref="Hold"/> may (a held Pinnacle fight).
        /// Pulled on every landblock heartbeat, so a hold attached after the copy loaded, or cleared at release, takes
        /// effect on the next heartbeat.
        /// </summary>
        public TimeSpan? UnloadIntervalOverride => Run?.UnloadIntervalOverride ?? Hold?.UnloadIntervalOverride;

        /// <summary>
        /// Open World Threads (R22): true for the run's WHOLE lifetime - this instance hosts a Thread run that has not
        /// Ended (Starting, Active or Cleared, held or not). While true, an outdoor group's footprint MEMBERS never unload
        /// on their own idle timer (EphemeralInstanceGroup.MemberIdleUnloadHeld, consulted by the landblock heartbeat), so
        /// a run's creatures, caches or players' loot on a member cannot vanish while the run lives. The group still
        /// empties out through the primary: its own window measures the grace, and its unload drains the members first
        /// (#1753's primary-last rule); ThreadDungeonManager.RunGroupGone then ends a run whose member is gone. False for
        /// every non-run instance (a Pinnacle hold, the /enter-instance-group seam), which keep #1753's per-member idle
        /// unload unchanged, and false the moment the run is marked Ended, which releases the members. Pulled on every
        /// heartbeat.
        /// </summary>
        public bool HoldsFootprintMembers => Run != null && Run.State != ACE.Server.ThreadDungeons.ThreadDungeonRunState.Ended;

        /// <summary>
        /// The landblocks this instance owns, or null for the default: the one landblock the creator loads, which
        /// is every ephemeral instance that existed before outdoor instance groups. Read once, by
        /// LandblockManager.GetLandblock when it registers the instance; its <see cref="EphemeralFootprint.Primary"/>
        /// must be the landblock that call loads. Set it before that call and never change it afterwards.
        /// </summary>
        public EphemeralFootprint Footprint { get; init; }

        /// <summary>
        /// landblock_instance rows this copy does not load, or null to load every row. Landblock.CreateWorldObjects
        /// applies it after the Thread content filter and before the factory, so it must be set before the copy's
        /// landblock is constructed (RealmManager.GetNewEphemeralLandblock's excludedRows) and never changed afterwards.
        /// </summary>
        public EphemeralRowExclusion ExcludedRows { get; init; }

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

            // Thread puzzle fail policy, ahead of every rule that admits: a Thread copy refuses a character the policy
            // removed from this run, and any character whose connection key is locked out, however they arrive - a
            // teleport, a relog inside the copy (the login reroute), a stale Owner or allowedPlayers entry. This is
            // also the guard for an OFFLINE same-key roster member, whom the lockout sweep never sees.
            if (Run != null && ACE.Server.ThreadDungeons.ThreadPuzzleFailPolicy.RefusesEntry(Run, player))
                return false;

            if (player == Owner)
                return true;

            // Group Threads (ruling R10): a Thread copy admits every character on its run's roster by GUID, so a
            // member with a key, and an owner or member who relogged (a new Player object), are all accepted.
            // Other ephemeral realms have no Run and keep the reference and fellowship rules below unchanged.
            if (Run != null && player != null && AcceptsRosterGuid(Run, player.Guid.Full))
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

        /// <summary>
        /// The pure half of the roster admission in <see cref="Accepts"/>: is this guid on the run's roster, and not
        /// removed from it by the puzzle fail policy (ThreadDungeonRun.MarkPuzzleRemoved)?
        /// </summary>
        internal static bool AcceptsRosterGuid(ACE.Server.ThreadDungeons.ThreadDungeonRun run, uint guid)
            => run != null && run.IsRosterMember(guid) && !run.IsPuzzleRemoved(guid);
    }
}
