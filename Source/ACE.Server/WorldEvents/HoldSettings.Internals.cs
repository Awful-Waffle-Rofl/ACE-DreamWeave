using System.Collections.Generic;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Hold numbers that were constants in code, exposed so they can be tuned without a rebuild. Each defaults to the old
    /// constant, so nothing changes until an admin sets one. The structural ones (search steps, turn speed, the cache ring)
    /// stay in code.
    /// </summary>
    public static partial class HoldSettings
    {
        static partial void RegisterInternals(List<HoldSetting> r)
        {
            r.Add(Num(GroupInternals, HoldKeys.HitIntervalSeconds, WorldEventHoldRules.DefaultHitIntervalSeconds, 0.5, 10, HoldApplies.Live, null, true,
                "A monster's first hit on a knight counts as this many seconds of attacking (time-based damage)."));
            r.Add(Num(GroupInternals, HoldKeys.HitIntervalMaxSeconds, WorldEventHoldRules.MaxHitIntervalSeconds, 1, 20, HoldApplies.Live, null, true,
                "The longest gap one hit on a knight may count for."));
            r.Add(Num(GroupInternals, HoldKeys.CapBurstSeconds, WorldEventHoldRules.DefaultCapBurstSeconds, 0, 10, HoldApplies.Live, null, true,
                "Burst allowed on top of the min_death_seconds rate, in seconds of it."));
            r.Add(Num(GroupInternals, HoldKeys.SpawnClearance, ProtectDef.MinSpawnClearance, 0, 50, HoldApplies.NextWave, null, true,
                "Waves spawn at least this many metres outside the knights' ring."));
            r.Add(Num(GroupInternals, HoldKeys.RangedSlack, WorldEventHoldRules.RangedHoldSlack, 0, 10, HoldApplies.Live, null, true,
                "A ranged monster closes in again once this many metres beyond its keep-distance."));
            r.Add(Num(GroupInternals, HoldKeys.HealerMemorySeconds, WorldEvent.ProtectedHealerMemorySeconds, 0, 120, HoldApplies.Live, null, true,
                "How long a player who healed a knight stays a hunter target."));
            r.Add(Num(GroupInternals, HoldKeys.WaveResolveGraceSeconds, WorldEvent.HoldWaveResolveGraceSeconds, 5, 120, HoldApplies.Live, null, true,
                "A wave the spawner never reported counts as resolved after this long."));
        }
    }
}
