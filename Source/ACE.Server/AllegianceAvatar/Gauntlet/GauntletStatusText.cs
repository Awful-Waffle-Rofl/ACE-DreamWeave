using System;
using System.Collections.Generic;

using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>The lines /avatar gauntlet shows a member. Pure: every input is handed in.</summary>
public static class GauntletStatusText
{
    public const string DisabledLine = "The Allegiance Gauntlet is not available right now.";

    /// <summary>
    /// The status of an allegiance's Gauntlet: the run (null when none is under way), the player's own death-hold seconds
    /// left, the Avatar's cooldown left (null when ready) and its banked best score in hundredths of a wave (0 for none).
    /// </summary>
    public static IReadOnlyList<string> Lines(bool enabled, GauntletRunInfo run, double holdSecondsLeft, TimeSpan? cooldownLeft, long bestCenti)
    {
        var lines = new List<string>();

        if (!enabled)
        {
            lines.Add(DisabledLine);
            return lines;
        }

        if (run == null)
        {
            lines.Add("Allegiance Gauntlet: no run under way. Buy one from the Avatar.");
        }
        else
        {
            lines.Add($"Allegiance Gauntlet: {StateText(run.State)}.");

            if (run.NpcAt != null && GauntletArenaHold.AdmitsIn(run.State))
                lines.Add($"The Gauntlet Gate stands beside the Avatar at {run.NpcAt}.");

            if (run.State is GauntletState.Active or GauntletState.Intermission or GauntletState.Ending)
            {
                lines.Add($"Wave {run.Wave} (level {run.Level}), {run.WavesCleared} cleared. {TimeLeftText(run)}");
                lines.Add($"{run.Headcount} fighting, {run.Fought} fought so far.");
            }
            else if (run.State == GauntletState.Staging)
            {
                lines.Add($"{TimeLeftText(run)} Enter through the Gauntlet Gate.");
            }
            else if (run.State == GauntletState.Rewarding)
            {
                lines.Add($"{run.Fought} fought. {TimeLeftText(run)}");
            }

            if (holdSecondsLeft > 0)
                lines.Add($"You are held for {(int)Math.Ceiling(holdSecondsLeft)} more seconds.");
        }

        lines.Add(cooldownLeft is { } left && left > TimeSpan.Zero
            ? $"Cooldown: {GauntletManager.CooldownText(left)} left."
            : "Cooldown: ready.");

        lines.Add(bestCenti > 0 ? $"Best score: {Player.FormatWaveScore(bestCenti)}." : "Best score: none yet.");

        return lines;
    }

    private static string TimeLeftText(GauntletRunInfo run) => $"{Math.Max(0, run.SecondsLeft)} s left.";

    internal static string StateText(GauntletState state) => state switch
    {
        GauntletState.Purchased => "bought, the arena is opening",
        GauntletState.Staging => "gathering",
        GauntletState.Active => "under way",
        GauntletState.Intermission => "between waves",
        GauntletState.Ending => "ending",
        GauntletState.Rewarding => "paying out",
        GauntletState.Closed => "over",
        GauntletState.Aborted => "called off",
        _ => state.ToString(),
    };
}
