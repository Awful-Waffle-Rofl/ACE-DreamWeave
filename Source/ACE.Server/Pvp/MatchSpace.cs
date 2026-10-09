namespace ACE.Server.Pvp
{
    /// <summary>
    /// An allocated match instance, as returned inside a successful <see cref="MatchSpaceAllocation"/>.
    ///   - <see cref="LandblockId"/> is the 16-bit landblock number (0x0066), as on <see cref="ArenaMap"/>.
    ///   - <see cref="Instance"/> is the full 32-bit ephemeral instance id; feed it to
    ///     <see cref="ArenaSpawnPosition.Build"/> to get each participant's teleport destination.
    ///   - <see cref="OwnerGuid"/> is the character guid of the first admitted player, who owns the
    ///     EphemeralRealm. Ownership grants nothing extra here: every participant is admitted explicitly.
    /// </summary>
    public sealed record MatchSpace(string MapKey, uint LandblockId, ushort RealmId, uint Instance, uint OwnerGuid)
    {
        /// <summary>
        /// The provider's reference to the live instance (a Landblock in the server). Readiness and release
        /// compare it by REFERENCE against what the instance id currently resolves to, so a recycled instance id
        /// that now belongs to some other copy is never mistaken for this one.
        /// </summary>
        internal object Handle { get; init; }
    }

    /// <summary>Why <see cref="IMatchSpaceProvider.Allocate"/> did not produce a space. None means it did.</summary>
    public enum MatchSpaceFailure
    {
        None,

        /// <summary>No map was supplied.</summary>
        NoMap,

        /// <summary>The admitted list was null or empty.</summary>
        NoParticipants,

        /// <summary>The admitted list contained a null entry.</summary>
        NullParticipant,

        /// <summary>The map has no spawn point at all, so there is nothing to validate an arrival against.</summary>
        NoSpawnPoints,

        /// <summary>The map's landblock number or a spawn cell is malformed.</summary>
        InvalidMap,

        /// <summary>The map's realm is not in the realm registry.</summary>
        RealmNotRegistered,

        /// <summary>Creating the instance threw. The exception is logged, never rethrown.</summary>
        CreateFailed,

        /// <summary>The instance was created but is a different landblock than the map names.</summary>
        LandblockMismatch,

        /// <summary>The landblock has a traversable overworld; ephemeral instances are dungeon-only.</summary>
        NotADungeon,

        /// <summary>The instance id is not an ephemeral id in the map's realm.</summary>
        InstanceMismatch,

        /// <summary>The instance carries no EphemeralRealm, so nobody could be admitted to it.</summary>
        AdmitFailed,

        /// <summary>A participant's arrival would be refused by the same check Player.Teleport runs.</summary>
        ParticipantRefused,

        /// <summary>Checking the new instance threw. The exception is logged and the instance disposed of.</summary>
        ValidationFailed,
    }

    /// <summary>Result of <see cref="IMatchSpaceProvider.Allocate"/>: a space, or a failure and a log-ready detail.</summary>
    public sealed record MatchSpaceAllocation(MatchSpace Space, MatchSpaceFailure Failure, string Detail)
    {
        public bool Succeeded => Space != null && Failure == MatchSpaceFailure.None;

        public static MatchSpaceAllocation Ok(MatchSpace space) => new MatchSpaceAllocation(space, MatchSpaceFailure.None, null);

        public static MatchSpaceAllocation Fail(MatchSpaceFailure failure, string detail) => new MatchSpaceAllocation(null, failure, detail);
    }

    /// <summary>What <see cref="IMatchSpaceProvider.GetReadiness"/> reports for an allocated space.</summary>
    public enum MatchSpaceReadiness
    {
        /// <summary>
        /// The instance is registered, but its population task has not finished (CreateWorldObjectsCompleted is
        /// still false). Players may be teleported in now - Player.OnTeleportComplete holds them until it finishes -
        /// but the match should not count down until Ready. A space that never leaves Loading had its population
        /// task fault; the coordinator's staging timeout is what ends that.
        /// </summary>
        Loading,

        /// <summary>The instance is registered and fully populated.</summary>
        Ready,

        /// <summary>The instance id no longer resolves to this space's landblock: it unloaded, or was never live.</summary>
        Gone,
    }
}
