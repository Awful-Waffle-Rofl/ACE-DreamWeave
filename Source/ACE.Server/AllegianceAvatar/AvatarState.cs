using System;

using ACE.Entity;

namespace ACE.Server.Avatars;

/// <summary>Stored as `allegiance_avatar.state`: 0 Bound, 1 Absorbed, 2 Retired.</summary>
public enum AvatarBindState : byte
{
    Bound = 0,
    Absorbed = 1,
    Retired = 2,
}

/// <summary>Where a seated Avatar stands: the mansion and the Keeper's own position at /avatar seat time.</summary>
public sealed record AvatarSeat(uint HouseGuid, Position Position);

/// <summary>
/// The in-memory state of one Avatar. AvatarManager is the only writer, and every write happens under its
/// lock. Readers outside the manager (the vendor panel, commands) read on the world thread and must treat a
/// read of several fields as a best-effort view; anything that decides a spend goes through the manager's
/// Favor reservation API instead.
///
/// Persisted fields round-trip through ShardAvatarStore. ReservedFavor, KeeperGuid, KeeperIsActing, Active
/// and Version are memory only and are rebuilt (or start at zero) after a restart.
/// </summary>
public sealed class AvatarState
{
    public uint AvatarId { get; internal set; }

    /// <summary>The bound monarch. Null once the Avatar is absorbed or retired.</summary>
    public uint? MonarchGuid { get; internal set; }

    public AvatarBindState State { get; internal set; }

    /// <summary>Lifetime contribution points. Never spent and never decays. Feeds the ledger and Favor, not the level.</summary>
    public ulong LifetimeXp { get; internal set; }

    /// <summary>
    /// The level pool: raw XP, a tunable share (allegiance_avatar_level_xp_fraction) of every counted grant members
    /// receive. The Avatar's level is the player-chart level at this total. Persisted.
    /// </summary>
    public ulong LevelXp { get; internal set; }

    /// <summary>Memory only. The fraction of a pool XP not yet credited to <see cref="LevelXp"/>; lost on restart.</summary>
    internal double LevelXpRemainder { get; set; }

    /// <summary>Spendable points, including the part currently reserved.</summary>
    public ulong Favor { get; internal set; }

    /// <summary>Favor held by an in-flight purchase. Memory only; never decays. Favor - ReservedFavor is spendable.</summary>
    public ulong ReservedFavor { get; internal set; }

    public uint? HeirGuid { get; internal set; }

    /// <summary>Null while the Avatar is wandering.</summary>
    public AvatarSeat Seat { get; internal set; }

    public DateTime? SeatedAtUtc { get; internal set; }

    public DateTime? LastContributionUtc { get; internal set; }

    /// <summary>The UTC date decay last ran for this Avatar.</summary>
    public DateTime? LastDecayDay { get; internal set; }

    /// <summary>The avatar_Id this Avatar was absorbed into, when State is Absorbed.</summary>
    public uint? AbsorbedIntoId { get; internal set; }

    /// <summary>
    /// The stored look document (look_Json), verbatim. Null means never dressed (the default look). Persisted.
    /// Written only by AvatarManager.TrySetLook, which bumps <see cref="Version"/>.
    /// </summary>
    public string LookJson { get; internal set; }

    /// <summary>
    /// Memory only: <see cref="LookJson"/> parsed, so no spawn path reads or parses anything. Set on load
    /// (ShardAvatarStore.FromEntity) and by AvatarManager.TrySetLook. Null for never dressed, and for a stored
    /// look that does not parse (AvatarLookJson.Parse logs the warning), which then spawns the default look.
    /// </summary>
    internal AvatarLook Look { get; set; }

    /// <summary>Who holds the Keeper's rights, from the last recompute. The monarch until a recompute says otherwise.</summary>
    public uint KeeperGuid { get; internal set; }

    /// <summary>True when someone other than the monarch holds the Keeper's rights.</summary>
    public bool KeeperIsActing { get; internal set; }

    /// <summary>True when the allegiance had at least minActiveAccounts active accounts at the last recompute.</summary>
    public bool Active { get; internal set; }

    /// <summary>
    /// Bumped on every change to who or what the Avatar is bound to: State, MonarchGuid, HeirGuid, Seat,
    /// AbsorbedIntoId, the Keeper, Active and Look. A confirmation popup captures it and cancels on mismatch. The
    /// meters (LifetimeXp, Favor, ReservedFavor) and the decay day do NOT bump it, so routine kills and
    /// decay never cancel an open popup.
    /// </summary>
    public int Version { get; internal set; }

    /// <summary>Distinct accounts active in the allegiance at the last recompute; the roster scale input.</summary>
    internal int ActiveAccounts { get; set; }

    /// <summary>
    /// Memory only. False until the first Keeper resolve after a load or a founding settles KeeperGuid;
    /// that first settle is silent, so a restart never re-announces a Keeper who was already acting.
    /// </summary>
    internal bool KeeperResolved { get; set; }

    /// <summary>Memory only. The monarch's account at the last Keeper resolve (0 until then); lets a login on an alt find this Avatar.</summary>
    internal uint MonarchAccountId { get; set; }

    public int Level(AvatarTuning t) => AvatarCurves.LevelForPool(LevelXp, AvatarCurves.ChartSource());

    /// <summary>Favor not held by a reservation.</summary>
    public ulong AvailableFavor => Favor > ReservedFavor ? Favor - ReservedFavor : 0;
}
