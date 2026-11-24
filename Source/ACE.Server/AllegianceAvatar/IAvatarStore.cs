using System;
using System.Collections.Generic;

using ACE.Database.Models.Shard;

namespace ACE.Server.Avatars;

/// <summary>
/// AvatarManager's persistence seam. ShardAvatarStore is the production implementation; tests use a fake.
/// The two loads are synchronous and run once at startup; they return NULL when the read FAILED, which is
/// not the same as empty. SaveBatch and Insert are queued: their callbacks run later, on another thread.
/// </summary>
public interface IAvatarStore
{
    List<AllegianceAvatar> LoadAll();

    List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc);

    void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done);

    void Insert(AllegianceAvatar row, Action<bool, uint> done);
}
