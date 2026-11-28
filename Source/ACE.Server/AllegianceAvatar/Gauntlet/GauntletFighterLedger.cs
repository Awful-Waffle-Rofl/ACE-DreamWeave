using System;
using System.Globalization;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// One run's Avatar fighter tallies for the per-wave telemetry line (plan step 7; no schema change, so a log line): the damage
/// its hits asked for before the intake cap, what the cap let through, and the health members' kits and heal spells restored.
/// Shared by every fighter of the run (a respawn included), written from the fighter's landblock thread and taken by the run
/// at each wave end, so it is locked.
/// </summary>
public sealed class GauntletFighterLedger
{
    private readonly object sync = new object();
    private double requested;
    private double taken;
    private long healed;

    /// <summary>One hit: <paramref name="asked"/> before the cap, <paramref name="allowed"/> after it. A heal (0 or less) is not a hit.</summary>
    public void AddHit(double asked, double allowed)
    {
        if (!(asked > 0) || !double.IsFinite(asked))
            return;

        lock (sync)
        {
            requested += asked;
            taken += double.IsFinite(allowed) ? Math.Max(0.0, allowed) : 0.0;
        }
    }

    /// <summary>Health a member's kit or heal spell restored.</summary>
    public void AddHeal(int amount)
    {
        if (amount <= 0)
            return;

        lock (sync)
            healed += amount;
    }

    /// <summary>The tallies since the last take, and zeroes them.</summary>
    public (double Requested, double Taken, long Healed) Take()
    {
        lock (sync)
        {
            var result = (requested, taken, healed);
            requested = 0;
            taken = 0;
            healed = 0;
            return result;
        }
    }

    /// <summary>
    /// The per-wave line (pure). Damage is the fighter's intake since the previous wave end: requested before the intake cap,
    /// taken after it (before overkill, so the last hit of a death may count more than the health it removed).
    /// </summary>
    public static string WaveLine(uint runKey, int wave, double requested, double taken, long healed, int deaths, double secondsAlive, double waveSeconds)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "[GAUNTLET] run {0} wave {1} fighter: damage requested {2:0}, taken after cap {3:0}, healed {4}, deaths {5}, alive {6:0} of {7:0} s",
            runKey, wave, Math.Max(0.0, requested), Math.Max(0.0, taken), Math.Max(0L, healed), Math.Max(0, deaths),
            Math.Max(0.0, secondsAlive), Math.Max(0.0, waveSeconds));
    }
}
