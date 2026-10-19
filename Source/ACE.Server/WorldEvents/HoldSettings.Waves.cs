using System;
using System.Collections.Generic;

using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

namespace ACE.Server.WorldEvents
{
    public static partial class HoldSettings
    {
        static partial void RegisterWaves(List<HoldSetting> r)
        {
            r.Add(Int(GroupWaves, HoldKeys.WaveCountBase, 6, 1, 20, HoldApplies.NextEvent, "protect.waves.base", false,
                "Waves the group must survive (the minimum wave count).", g => F(g.Protect?.Waves?.Base ?? 0)));
            r.Add(Num(GroupWaves, HoldKeys.WaveCountPerPlayer, 0.25, 0, 5, HoldApplies.NextEvent, "protect.waves.perParticipant", false,
                "Extra waves per player.", g => F(g.Protect?.Waves?.PerParticipant ?? 0)));
            r.Add(Int(GroupWaves, HoldKeys.WaveCountCap, 10, 1, 20, HoldApplies.NextEvent, "protect.waves.cap", false,
                "The most waves (also limited by max_waves).", g => F(g.Protect?.Waves?.Cap ?? 0)));
            r.Add(Int(GroupWaves, HoldKeys.MaxWaves, ProtectDef.MaxHoldWaves, 1, 20, HoldApplies.NextEvent, null, true,
                "Hard ceiling on waves for any hold (was a constant)."));

            r.Add(Int(GroupWaves, HoldKeys.WaveSizeBase, 1, 1, 50, HoldApplies.NextWave, "protect.waveSize.base", false,
                "Monsters in a wave before any player is counted (the base wave size).", g => F(g.Protect?.WaveSize?.Base ?? 0)));
            r.Add(Num(GroupWaves, HoldKeys.WaveSizePerPlayer, 2.5, 0, 20, HoldApplies.NextWave, "protect.waveSize.perParticipant", false,
                "Extra monsters per player (per-player growth).", g => F(g.Protect?.WaveSize?.PerParticipant ?? 0)));
            r.Add(Int(GroupWaves, HoldKeys.WaveSizeCap, 40, 1, 200, HoldApplies.NextWave, "protect.waveSize.cap", false,
                "The largest a wave gets (the max size).", g => F(g.Protect?.WaveSize?.Cap ?? 0)));
            r.Add(Int(GroupWaves, HoldKeys.WaveSizeBasePlayers, 0, 0, 35, HoldApplies.NextWave, null, true,
                "The player count the base wave size covers: growth starts after this many players (0 = from the first)."));
            r.Add(Int(GroupWaves, HoldKeys.WaveSizeMaxPlayers, 0, 0, 100, HoldApplies.NextWave, null, true,
                "Growth stops at this many players (0 = no limit; the cap still applies)."));
            r.Add(Num(GroupWaves, HoldKeys.FinalWaveBonus, 0.25, 0, 2, HoldApplies.NextWave, null, true,
                "The final wave is this share bigger, at least one more (0 = no bonus; was a constant)."));

            r.Add(Num(GroupWaves, HoldKeys.FirstWaveDelaySeconds, HoldObjective.PlacementGraceSeconds, 0, 120, HoldApplies.Live, null, true,
                "The first wave may come once the knights stand, or this long after the start (was the 5 s placement grace)."));
            r.Add(Num(GroupWaves, HoldKeys.WaveIntervalSeconds, 40, 0, 600, HoldApplies.Live, "protect.waveIntervalSeconds", false,
                "The next wave comes after this long (wave interval).", g => F(g.Protect?.WaveIntervalSeconds ?? 0)));
            r.Add(Num(GroupWaves, HoldKeys.BreatherSeconds, ProtectDef.DefaultMinWaveGapSeconds, 0, 60, HoldApplies.Live, "protect.minWaveGapSeconds", false,
                "Never two waves closer together than this (the breather).", g => F(g.Protect?.MinWaveGapSeconds ?? 0)));
            r.Add(Num(GroupWaves, HoldKeys.MaxWaveIntervalSeconds, 75, 0, 600, HoldApplies.Live, "protect.maxWaveIntervalSeconds", false,
                "The next wave comes after this long whatever is alive (0 = no ceiling).", g => F(g.Protect?.MaxWaveIntervalSeconds ?? 0)));
            r.Add(Num(GroupWaves, HoldKeys.NextWaveAliveShare, ProtectDef.DefaultNextWaveAliveShare, 0, 1, HoldApplies.Live, "protect.nextWaveAliveShare", false,
                "The next wave may come early once no more than this share of the last is alive.", g => F(g.Protect?.NextWaveAliveShare ?? 0)));

            r.Add(Num(GroupWaves, HoldKeys.SpawnRadius, 26, 0, 80, HoldApplies.NextWave, "protect.spawnRadius", false,
                "Metres from the anchor the waves spawn at (without portals).", g => F(g.Protect?.SpawnRadius ?? 0)));
            r.Add(Int(GroupWaves, HoldKeys.PortalCount, 4, 1, 8, HoldApplies.NextEvent, "protect.portals.count", false,
                "Spawn portals placed around the ring.", g => F(g.Protect?.Portals?.Count ?? 0)));
            r.Add(Num(GroupWaves, HoldKeys.PortalRadius, 30, 5, 80, HoldApplies.NextEvent, "protect.portals.radius", false,
                "Metres from the anchor the portals stand.", g => F(g.Protect?.Portals?.Radius ?? 0)));

            r.Add(Int(GroupWaves, HoldKeys.HoldSeconds, 480, 60, 3600, HoldApplies.Live, "holdSeconds", false,
                "Reaching this with enough knights standing is a win (the hold timer). Must fit inside max_seconds.", g => F(g.HoldSeconds)));
            r.Add(Int(GroupWaves, HoldKeys.MaxSeconds, WorldEvent.DefaultMaxDurationSeconds, WorldEvent.DefaultMinDurationSeconds + 60, 3600, HoldApplies.NextEvent, null, true,
                "The event time limit for a Hold run (was the 600 s default); the run times out after this."));
            r.Add(Int(GroupWaves, HoldKeys.AbandonSeconds, WorldEvent.DefaultAbandonAfterSeconds, 0, 3600, HoldApplies.NextEvent, null, true,
                "A Hold run with nobody present ends after this long (0 = never). --abandon still wins for one run."));
        }

        /// <summary>
        /// A goals.json count with any of its settings laid over it: a fresh copy when one is set (marked as owning the dials
        /// it sets, so the global world_events_wave_count_* does not replace them), else the file's own object.
        /// </summary>
        public static ScaledCount EffectiveCount(ScaledCount file, string baseKey, string perKey, string capKey, string fromPlayersKey, string upToPlayersKey)
        {
            var setBase = IsSet(baseKey);
            var setPer = IsSet(perKey);
            var setCap = IsSet(capKey);
            var setFrom = fromPlayersKey != null && IsSet(fromPlayersKey);
            var setUpTo = upToPlayersKey != null && IsSet(upToPlayersKey);

            if (!setBase && !setPer && !setCap && !setFrom && !setUpTo)
                return file;

            if (file == null && !setBase)
                return null;

            var copy = new ScaledCount
            {
                Base = setBase ? Integer(baseKey, 1) : file.Base,
                PerParticipant = setPer ? Number(perKey, 0) : file?.PerParticipant ?? 0,
                Cap = setCap ? Integer(capKey, 1) : file?.Cap ?? 0,
                OwnsBase = setBase,
                OwnsPerParticipant = setPer,
                OwnsCap = setCap,
                PlayersFrom = setFrom ? Integer(fromPlayersKey, 0) : 0,
                PlayersUpTo = setUpTo ? Integer(upToPlayersKey, 0) : 0
            };

            // A count's cap is never under its base.
            if (copy.Cap < copy.Base)
            {
                copy.Cap = copy.Base;
                copy.OwnsCap = true;
            }

            return copy;
        }

        /// <summary>The run's time limit and abandon timer for a Hold goal (max_seconds, abandon_seconds); the request's own when not set.</summary>
        public static void ApplyRunTimers(WorldEventRequest request, GoalDef goal)
        {
            if (request == null || goal == null || goal.TypeKind != GoalType.Hold)
                return;

            if (IsSet(HoldKeys.MaxSeconds) && request.MaxDurationSeconds == WorldEvent.DefaultMaxDurationSeconds)
                request.MaxDurationSeconds = Integer(HoldKeys.MaxSeconds, WorldEvent.DefaultMaxDurationSeconds);

            if (IsSet(HoldKeys.AbandonSeconds) && !request.AbandonExplicit)
                request.AbandonAfterSeconds = Integer(HoldKeys.AbandonSeconds, WorldEvent.DefaultAbandonAfterSeconds);
        }
    }
}
