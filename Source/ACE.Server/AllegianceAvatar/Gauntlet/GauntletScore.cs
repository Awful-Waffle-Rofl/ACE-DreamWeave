using System;
using System.Collections.Generic;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>One spawned wave creature: its role, its max health at spawn, the damage its history totals, and whether it is still alive.</summary>
public readonly record struct GauntletSpawnedRow(string Role, double Max, double Dealt, bool Alive);

/// <summary>
/// Planned creatures of one role that never entered the world, with the health to assume for each when no creature of that
/// role spawned (the plan's HealthCurveTarget times its health multiplier for the role).
/// </summary>
public readonly record struct GauntletUnspawned(string Role, int Count, double FallbackMax);

/// <summary>
/// The Gauntlet score, in hundredths of a wave (the Proving Grounds rule, Player_WaveChallenge.cs:770-772), and the
/// live-wave fraction it is built from (the GetLiveWaveFraction rule, :708-748). Pure.
/// </summary>
public static class GauntletScore
{
    /// <summary>The partial credit on a timed-out wave stops here, so a partial wave never ties a full clear.</summary>
    public const long PartialCap = 99;

    /// <summary>
    /// cleared x 100, plus (only on a timer expiry mid-wave) min(99, floor(100 x fraction)). A fraction outside [0, 1] or NaN is clamped.
    /// </summary>
    public static long Centi(int cleared, double fraction, bool timedOutMidWave)
    {
        var score = Math.Max(0, cleared) * 100L;

        if (!timedOutMidWave)
            return score;

        var f = double.IsNaN(fraction) ? 0.0 : Math.Min(1.0, Math.Max(0.0, fraction));
        return score + Math.Min(PartialCap, (long)Math.Floor(f * 100));
    }

    /// <summary>
    /// The share (0..1) of the wave's health that has been destroyed. A dead creature counts its whole spawn max; a living one
    /// counts its damage clamped to [0, its max], so an overkill cannot push the wave past 100%. A row with no max is skipped.
    /// <paramref name="unspawned"/> adds, for planned creatures that never entered the world, a denominator share and no
    /// numerator: each counts the mean spawn max of the spawned rows of its role, or its own fallback when none spawned.
    /// With no spawned rows and no unspawned entries the result is 0.
    /// </summary>
    public static double Fraction(IReadOnlyList<GauntletSpawnedRow> spawned, IReadOnlyList<GauntletUnspawned> unspawned = null)
    {
        double sumMax = 0;
        double sumDealt = 0;

        if (spawned != null)
        {
            foreach (var row in spawned)
            {
                if (row.Max <= 0)
                    continue;

                sumMax += row.Max;

                var dealt = row.Alive ? row.Dealt : row.Max;
                sumDealt += Math.Min(row.Max, Math.Max(0, dealt));
            }
        }

        if (unspawned != null)
        {
            foreach (var entry in unspawned)
            {
                if (entry.Count <= 0)
                    continue;

                var estimate = RoleMean(spawned, entry.Role) ?? entry.FallbackMax;

                if (!double.IsFinite(estimate) || estimate <= 0)
                    continue;

                sumMax += estimate * entry.Count;
            }
        }

        if (sumMax <= 0)
            return 0;

        return Math.Min(1.0, Math.Max(0.0, sumDealt / sumMax));
    }

    private static double? RoleMean(IReadOnlyList<GauntletSpawnedRow> spawned, string role)
    {
        if (spawned == null)
            return null;

        double total = 0;
        var n = 0;

        foreach (var row in spawned)
        {
            if (row.Max <= 0 || !string.Equals(row.Role, role, StringComparison.Ordinal))
                continue;

            total += row.Max;
            n++;
        }

        return n == 0 ? null : total / n;
    }

    /// <summary>
    /// True when <paramref name="score"/> is strictly above the server #1 score captured when the run went Active (the Proving
    /// Grounds snapshot rule). A tie never announces, and a zero score never does.
    /// </summary>
    public static bool IsNewRecord(long score, long snapshotScore) => score > 0 && score > snapshotScore;
}
