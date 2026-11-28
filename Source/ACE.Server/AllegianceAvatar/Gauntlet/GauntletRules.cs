using System;
using System.Collections.Generic;

using ACE.Server.ThreadDungeons;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// The pure Gauntlet wave rules: wave level, boss cadence, time limit, trash count, headcount clamp, crate eligibility and
/// the portal-wave config. Built over one <see cref="GauntletTuning"/> value; it reads nothing live, so a test drives it
/// with a hand-built tuning and no PropertyManager. Pass an already sanitized tuning (<see cref="GauntletTuning.FromPropertyManager"/>
/// and <see cref="GauntletTuning.Sanitized"/> do that).
/// </summary>
public sealed class GauntletRules
{
    private readonly GauntletTuning t;

    public GauntletRules(GauntletTuning tuning)
    {
        t = tuning ?? throw new ArgumentNullException(nameof(tuning));
    }

    /// <summary>L(n) = start_level + level_step x (n - 1). There is no maximum; the multiply and add saturate at int.MaxValue. A wave below 1 is read as wave 1.</summary>
    public int LevelOf(int wave)
    {
        var n = Math.Max(1, wave);
        var start = Math.Clamp(t.StartLevel, 1L, int.MaxValue);
        var step = Math.Clamp(t.LevelStep, 0L, int.MaxValue);

        // Saturating start + step x (n - 1): with both bounded by int.MaxValue the product fits a long, but an unsanitized
        // tuning is guarded here too.
        if (step > 0 && n - 1L > (int.MaxValue - start) / step)
            return int.MaxValue;

        return (int)(start + step * (n - 1L));
    }

    /// <summary>True on every boss_every-th wave (5, 10, 15, ...).</summary>
    public bool IsBossWave(int wave) => wave >= 1 && wave % Math.Max(1L, t.BossEvery) == 0;

    /// <summary>wave_seconds, plus boss_wave_bonus_seconds on a boss wave. Flat on purpose: the waves get harder, the clock does not.</summary>
    public int TimeLimitSeconds(int wave)
    {
        var seconds = t.WaveSeconds + (IsBossWave(wave) ? t.BossWaveBonusSeconds : 0L);
        return (int)Math.Clamp(seconds, 1L, int.MaxValue);
    }

    /// <summary>
    /// round(base + per_player x H) with H clamped, capped at max_alive; a boss wave then multiplies by boss_wave_trash_fraction
    /// (the cap applies first). Halves round away from zero. A plain wave is at least 1, or it would clear the moment it started.
    /// </summary>
    public int TrashCount(int headcount, bool boss)
    {
        var h = ClampHeadcount(headcount);
        var count = Math.Round(t.WaveBaseCount + t.WaveCountPerPlayer * h, MidpointRounding.AwayFromZero);
        count = Math.Min(count, (double)t.MaxAlive);

        if (boss)
            return (int)Math.Max(0, Math.Round(count * t.BossWaveTrashFraction, MidpointRounding.AwayFromZero));

        return (int)Math.Max(1, count);
    }

    /// <summary>
    /// The per-trash health multiplier that keeps the wave's total trash pool at <paramref name="effort"/> times the solo pool
    /// (owner ruling 2026-11-26, the way Threads do it): E x count(1) / count(H), so count(H) x mult = count(1) x E and every
    /// member carries the same load at any group size. Both counts are this wave type's (a boss wave uses its boss-wave trash
    /// count on both sides) and the cap on count(H) is already in them, so past max_alive the multiplier rises to compensate.
    /// With either count at 0 (a boss wave whose trash fraction is 0) there is no pool to hold and it reads as
    /// <paramref name="effort"/>. A non-finite or sub-1 effort reads as 1.
    /// </summary>
    public double TrashHealthMultiplier(int headcount, bool boss, double effort)
    {
        var e = double.IsFinite(effort) && effort >= 1.0 ? effort : 1.0;
        var solo = TrashCount(1, boss);
        var group = TrashCount(headcount, boss);

        if (solo <= 0 || group <= 0)
            return e;

        return e * solo / group;
    }

    /// <summary>
    /// The felt loss of a fallen fighter (owner direction 2026-11-25): the seconds it is held out after its death, given how
    /// many times it had already fallen this run. min(fighter_respawn_max_seconds, fighter_respawn_seconds +
    /// fighter_respawn_step_seconds x prior deaths), saturating, never below 0. Defaults: 60, 80, 100, 120, 120, ...
    /// </summary>
    public long FighterRespawnSeconds(int priorDeaths)
    {
        var prior = Math.Max(0, priorDeaths);
        var baseSeconds = Math.Max(0L, t.FighterRespawnSeconds);
        var step = Math.Max(0L, t.FighterRespawnStepSeconds);
        var max = Math.Max(baseSeconds, t.FighterRespawnMaxSeconds);

        // saturating: a garbled step must not wrap the product negative
        var grown = step > 0 && prior > (max - baseSeconds) / step ? max : baseSeconds + step * prior;

        return Math.Min(max, grown);
    }

    /// <summary>The headcount a wave scales with: [1, headcount_cap].</summary>
    public int ClampHeadcount(int headcount) => (int)Math.Clamp((long)headcount, 1L, Math.Max(1L, t.HeadcountCap));

    /// <summary>
    /// A member earns a crate with contact in at least min_contact_waves distinct waves of this run, or in every wave the run
    /// reached if it reached fewer. Waves outside 1..wavesReached never count. A run that reached no wave pays nobody.
    /// </summary>
    public bool IsEligible(IReadOnlySet<int> contactWaves, int wavesReached)
    {
        if (contactWaves == null || wavesReached < 1)
            return false;

        return CountedWaves(contactWaves, wavesReached) >= RequiredWaves(wavesReached);
    }

    /// <summary>The waves a crate needs: min_contact_waves, or every wave reached if fewer; at least 1.</summary>
    public int RequiredWaves(int wavesReached) => (int)Math.Max(1L, Math.Min(Math.Max(1L, t.MinContactWaves), Math.Max(1, wavesReached)));

    /// <summary>The waves in 1..wavesReached among <paramref name="contactWaves"/>.</summary>
    public static int CountedWaves(IReadOnlySet<int> contactWaves, int wavesReached)
    {
        var counted = 0;

        if (contactWaves != null)
            foreach (var wave in contactWaves)
                if (wave >= 1 && wave <= wavesReached)
                    counted++;

        return counted;
    }

    /// <summary>
    /// The portal-wave config for one wave: <paramref name="portals"/> portals (at least 1, never more than there are enemies),
    /// each streaming between floor(target / portals) and ceil(target / portals) enemies, so the stream averages the target.
    /// A positive target never gives a portal zero. The stagger and distance bounds are the tunables.
    /// </summary>
    public IPortalWaveConfig PortalConfig(int target, int portals)
    {
        var enemies = Math.Max(0, target);
        var p = Math.Max(1, portals);

        if (enemies > 0)
            p = Math.Min(p, enemies);

        var floor = enemies / p;
        var ceil = (enemies + p - 1) / p;

        return new GauntletPortalConfig
        {
            PortalsMin = p,
            PortalsMax = p,
            PerPortalMin = floor,
            PerPortalMax = ceil,
            StaggerMin = t.StaggerMinSeconds,
            StaggerMax = t.StaggerMaxSeconds,
            PortalMinDistance = (float)t.PortalMinDistance,
            PortalMaxDistance = (float)t.PortalMaxDistance,
        };
    }
}

/// <summary>The Gauntlet's <see cref="IPortalWaveConfig"/>: the Defense portal recipe's scales (0.6 outdoors, 0.3 in a dungeon).</summary>
public sealed class GauntletPortalConfig : IPortalWaveConfig
{
    public int PortalsMin { get; init; }
    public int PortalsMax { get; init; }
    public int PerPortalMin { get; init; }
    public int PerPortalMax { get; init; }
    public double StaggerMin { get; init; }
    public double StaggerMax { get; init; }
    public float PortalMinDistance { get; init; }
    public float PortalMaxDistance { get; init; }

    public double PortalScale(bool isOutdoor) => isOutdoor ? 0.6 : 0.3;
}
