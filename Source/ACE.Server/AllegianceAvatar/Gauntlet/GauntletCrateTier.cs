using System;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// The crate a finished run pays: which rewards.json profile (gauntlet_t0 to gauntlet_t6) and what multiplier. Pure.
/// WorldEventRewardCrate clamps the multiplier to (0, 2] (WorldEventRewardCrate.cs:67-72), so m stays inside [1, 2].
/// </summary>
public static class GauntletCrateTier
{
    public const string ProfilePrefix = "gauntlet_t";

    /// <summary>
    /// tier = min(floor(cleared / 5), maxTier). Below the top tier, m = 1 + ((cleared mod 5) + partial / 100) / 5, which is 1.0 to
    /// 1.998. At the top tier, m = min(2, 1 + (cleared - 5 x maxTier) / 10) and the partial no longer counts. A run that cleared
    /// nothing pays gauntlet_t0 at 1, whatever its partial. maxTier is held to [0, <see cref="GauntletTuning.CrateMaxTierCeiling"/>]
    /// and partial to [0, 99].
    /// </summary>
    public static (string Profile, double Multiplier) For(int cleared, int partial, int maxTier)
    {
        var max = Math.Clamp(maxTier, 0, GauntletTuning.CrateMaxTierCeiling);
        var waves = Math.Max(0, cleared);

        if (waves == 0)
            return (ProfilePrefix + "0", 1.0);

        var tier = Math.Min(waves / 5, max);
        var profile = ProfilePrefix + tier;

        if (tier < max)
        {
            var p = Math.Clamp(partial, 0, 99);
            return (profile, 1.0 + ((waves % 5) + p / 100.0) / 5.0);
        }

        return (profile, Math.Min(2.0, 1.0 + (waves - 5L * max) / 10.0));
    }
}
