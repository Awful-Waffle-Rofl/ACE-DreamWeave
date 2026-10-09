using System;

namespace ACE.Server.WaveEncounters
{
    /// <summary>What the tick does with one live encounter this heartbeat.</summary>
    public enum WaveTickAction
    {
        /// <summary>A wave is standing, or the breather after a cleared wave has not run out yet.</summary>
        Wait,

        /// <summary>The next wave is due: spawn it.</summary>
        SpawnWave,

        /// <summary>The final wave is cleared: the encounter is won.</summary>
        Win,
    }

    /// <summary>Why an encounter ended. Win is the only success.</summary>
    public enum WaveEndReason
    {
        None,
        Win,
        Wiped,
        Ttl,
        LandblockUnloaded,
        AnchorGone,
        SpawnFailed,
        Stopped,
    }

    /// <summary>
    /// The pure decisions of the object-anchored wave runner: the per-heartbeat tick decision, the reap's
    /// end test and the start refusals. No world, no clock, no PropertyManager - every input is a parameter,
    /// so the tests pin behaviour without standing up a landblock.
    /// </summary>
    public static class WaveEncounterRules
    {
        /// <summary>
        /// The tick decision. <paramref name="currentWave"/> is the wave last spawned (0 before wave 1);
        /// <paramref name="aliveInWave"/> is how many of its creatures are still standing;
        /// <paramref name="nextWaveDue"/> is when the next wave may spawn (set at start for wave 1, and by the
        /// death hook when a wave empties).
        ///
        /// A standing wave always waits - even past a due time - so a wave can never spawn on top of the
        /// previous one. An empty final wave is a win whatever the due time says. An empty earlier wave with
        /// no due time recorded (a creature removed without dying, found by the reap) spawns the next wave
        /// now rather than stalling to the TTL.
        /// </summary>
        public static WaveTickAction Decide(int currentWave, int totalWaves, int aliveInWave, DateTime? nextWaveDue, DateTime now)
        {
            if (totalWaves < 1)
                return WaveTickAction.Wait;

            if (currentWave > 0 && aliveInWave > 0)
                return WaveTickAction.Wait;

            if (currentWave >= totalWaves)
                return WaveTickAction.Win;

            if (nextWaveDue == null || now >= nextWaveDue.Value)
                return WaveTickAction.SpawnWave;

            return WaveTickAction.Wait;
        }

        /// <summary>
        /// The reap's end test, in priority order. The landblock and the anchor come first: an unloaded
        /// landblock has already destroyed every creature without running Die(), so no death hook will ever
        /// report again and the encounter must be ended by polling. Then the TTL, then the wipe / walk-away
        /// grace. A TTL or grace of zero or less disables that test.
        /// </summary>
        public static bool ShouldEnd(bool landblockLoaded, bool anchorAlive, TimeSpan elapsed, TimeSpan ttl,
            TimeSpan sinceLastPresence, TimeSpan wipeGrace, out WaveEndReason reason)
        {
            reason = WaveEndReason.None;

            if (!landblockLoaded)
                reason = WaveEndReason.LandblockUnloaded;
            else if (!anchorAlive)
                reason = WaveEndReason.AnchorGone;
            else if (ttl > TimeSpan.Zero && elapsed >= ttl)
                reason = WaveEndReason.Ttl;
            else if (wipeGrace > TimeSpan.Zero && sinceLastPresence >= wipeGrace)
                reason = WaveEndReason.Wiped;

            return reason != WaveEndReason.None;
        }

        /// <summary>Only a cleared final wave is a win; every other end is a failure.</summary>
        public static bool IsWin(WaveEndReason reason) => reason == WaveEndReason.Win;

        /// <summary>
        /// The start refusal, or null when the anchor may start. Running is checked before the cooldown: an
        /// anchor with a live encounter says so, whatever its clock says.
        /// </summary>
        public static WaveStartRefusal CheckStart(bool running, DateTime? cooldownUntil, DateTime now, out TimeSpan cooldownRemaining)
        {
            cooldownRemaining = TimeSpan.Zero;

            if (running)
                return WaveStartRefusal.Running;

            if (cooldownUntil != null && now < cooldownUntil.Value)
            {
                cooldownRemaining = cooldownUntil.Value - now;
                return WaveStartRefusal.Cooldown;
            }

            return WaveStartRefusal.None;
        }

        /// <summary>
        /// Whole minutes left on a cooldown, for the refusal line: rounded UP so the line never says "0
        /// minutes" while the anchor still refuses, and never less than 1.
        /// </summary>
        public static int CooldownMinutes(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero)
                return 1;

            return Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        }

        /// <summary>"1 minute" / "N minutes".</summary>
        public static string MinutesText(int minutes) => minutes == 1 ? "1 minute" : $"{minutes} minutes";

        /// <summary>
        /// The final wave's clear-credit rule (owner ruling: last creature standing). The runner hands the
        /// clear quest to a creature only when the final wave is up and exactly one of its creatures is still
        /// alive; before that, nobody carries it, so the first boss to fall credits nothing.
        /// </summary>
        public static bool ShouldAssignClearQuest(int currentWave, int totalWaves, int aliveInWave, bool hasClearQuest)
            => hasClearQuest && totalWaves > 0 && currentWave == totalWaves && aliveInWave == 1;
    }

    public enum WaveStartRefusal
    {
        None,
        Running,
        Cooldown,
    }
}
