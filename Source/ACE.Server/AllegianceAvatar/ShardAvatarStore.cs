using System;
using System.Collections.Generic;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;

namespace ACE.Server.Avatars;

/// <summary>
/// The production IAvatarStore over DatabaseManager.Shard, plus the one-way mappings between AvatarState and
/// the EF entity. No Biota type is involved anywhere here. The loads go straight to BaseDatabase (synchronous,
/// startup only); the writes go through SerializedShardDatabase's queue, whose DAO methods already run the
/// one multi-statement write inside the execution strategy.
/// </summary>
public sealed class ShardAvatarStore : IAvatarStore
{
    public List<AllegianceAvatar> LoadAll() => DatabaseManager.Shard.BaseDatabase.GetAllAvatars();

    public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) =>
        DatabaseManager.Shard.BaseDatabase.GetAvatarContributionsSince(dayUtc);

    public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done) =>
        DatabaseManager.Shard.SaveAvatarBatch(a, r, done);

    public void Insert(AllegianceAvatar row, Action<bool, uint> done) =>
        DatabaseManager.Shard.InsertAvatar(row, done);

    /// <summary>A fresh, detached entity carrying the state's absolute values. created_At is left to the DAO.</summary>
    internal static AllegianceAvatar ToEntity(AvatarState s)
    {
        var e = new AllegianceAvatar
        {
            AvatarId = s.AvatarId,
            MonarchGuid = s.MonarchGuid,
            State = (byte)s.State,
            LifetimeXp = s.LifetimeXp,
            LevelXp = s.LevelXp,
            Favor = s.Favor,
            HeirGuid = s.HeirGuid,
            SeatedAt = s.SeatedAtUtc,
            AbsorbedIntoId = s.AbsorbedIntoId,
            LastContributionAt = s.LastContributionUtc,
            LastDecayDay = s.LastDecayDay,
            LookJson = s.LookJson,
            BlessingBoughtAt = s.BlessingBoughtUtc,
            LastEventAt = s.LastEventUtc,
            EventsOnDay = s.EventsOnDay,
        };

        var seat = s.Seat;
        if (seat != null)
        {
            var p = seat.Position;
            e.SeatHouseGuid = seat.HouseGuid;
            e.SeatCell = p.Cell;
            e.SeatInstance = p.Instance;
            e.SeatX = p.PositionX;
            e.SeatY = p.PositionY;
            e.SeatZ = p.PositionZ;
            e.SeatQw = p.RotationW;
            e.SeatQx = p.RotationX;
            e.SeatQy = p.RotationY;
            e.SeatQz = p.RotationZ;
        }

        return e;
    }

    /// <summary>
    /// A new state from a stored row. A seat is restored only when every seat column is present. The look is parsed
    /// here, once, so the spawn path reads it from memory; a look that does not parse is null (the default look).
    /// </summary>
    internal static AvatarState FromEntity(AllegianceAvatar e)
    {
        var s = new AvatarState
        {
            AvatarId = e.AvatarId,
            MonarchGuid = e.MonarchGuid,
            State = Enum.IsDefined(typeof(AvatarBindState), e.State) ? (AvatarBindState)e.State : AvatarBindState.Retired,
            LifetimeXp = e.LifetimeXp,
            LevelXp = e.LevelXp,
            Favor = e.Favor,
            HeirGuid = e.HeirGuid,
            SeatedAtUtc = AsUtc(e.SeatedAt),
            AbsorbedIntoId = e.AbsorbedIntoId,
            LastContributionUtc = AsUtc(e.LastContributionAt),
            LastDecayDay = e.LastDecayDay.HasValue ? DateTime.SpecifyKind(e.LastDecayDay.Value.Date, DateTimeKind.Utc) : null,
            KeeperGuid = e.MonarchGuid ?? 0,
            LookJson = e.LookJson,
            Look = AvatarLookJson.Parse(e.LookJson),
            BlessingBoughtUtc = AsUtc(e.BlessingBoughtAt),
            LastEventUtc = AsUtc(e.LastEventAt),
            EventsOnDay = e.EventsOnDay,
        };

        if (e.SeatHouseGuid.HasValue && e.SeatCell.HasValue && e.SeatInstance.HasValue
            && e.SeatX.HasValue && e.SeatY.HasValue && e.SeatZ.HasValue
            && e.SeatQw.HasValue && e.SeatQx.HasValue && e.SeatQy.HasValue && e.SeatQz.HasValue)
        {
            var position = new Position(e.SeatCell.Value, e.SeatX.Value, e.SeatY.Value, e.SeatZ.Value,
                e.SeatQx.Value, e.SeatQy.Value, e.SeatQz.Value, e.SeatQw.Value, e.SeatInstance.Value);
            s.Seat = new AvatarSeat(e.SeatHouseGuid.Value, position);
        }

        return s;
    }

    // The columns are `datetime` and always written as UTC; EF reads them back as Unspecified.
    private static DateTime? AsUtc(DateTime? value) => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
}
