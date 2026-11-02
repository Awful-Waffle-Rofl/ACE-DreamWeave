using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The death-cause marker for lethal Unraveling (Open World Threads R18, <c>unravel_lethal</c> = true). A death caused
    /// by the Unraveling write must not cost a gem entry. In this codebase a Thread death sends the player back to the
    /// entry stamp OUTSIDE the copy, and the entry is spent on the gem use that re-enters (ThreadDungeonGemHandler,
    /// DecideEntryCharge), so the exemption is a one-shot re-entry credit on the run
    /// (<see cref="ThreadDungeonRun.GrantUnravelReentry"/>) that the next gem use consumes as a Free charge.
    ///
    /// The marker is latched by the perimeter write just BEFORE a write that would kill, and read by Player.Die on the same
    /// thread. A mark that is never followed by a death (the player was healed in between) expires after
    /// <see cref="MarkWindow"/>, so it cannot exempt a later, unrelated death. A backstop snap-back never damages, so it
    /// never kills and needs no marker.
    /// </summary>
    public static class ThreadUnravelDeath
    {
        public static readonly TimeSpan MarkWindow = TimeSpan.FromSeconds(10);

        private static readonly ConcurrentDictionary<uint, long> marks = new ConcurrentDictionary<uint, long>();

        /// <summary>The clock in <see cref="Stopwatch"/> ticks. Tests replace it.</summary>
        internal static Func<long> Clock = Stopwatch.GetTimestamp;

        /// <summary>Latches "the next death of this player is an Unraveling death".</summary>
        public static void Mark(uint playerGuid) => marks[playerGuid] = Clock();

        /// <summary>True once per mark, and only inside the window. Always clears the mark.</summary>
        public static bool TryConsume(uint playerGuid)
        {
            if (!marks.TryRemove(playerGuid, out var stamp))
                return false;

            return (Clock() - stamp) <= (long)(MarkWindow.TotalSeconds * Stopwatch.Frequency);
        }

        /// <summary>Test hook: forget every mark.</summary>
        internal static void Reset() => marks.Clear();

        /// <summary>
        /// Player.Die hook (a death latched as inside a live run). Consumes the mark and, when it was an Unraveling death,
        /// credits the run so the player's next re-entry is free. Never throws into the death sequence.
        /// </summary>
        public static void OnPlayerDied(uint playerGuid, uint instance)
        {
            try
            {
                if (!TryConsume(playerGuid))
                    return;

                ThreadDungeonManager.GetRun(instance)?.GrantUnravelReentry(playerGuid);
            }
            catch (Exception ex)
            {
                log4net.LogManager.GetLogger(typeof(ThreadUnravelDeath)).Error("[OWT] unravel death hook threw", ex);
            }
        }
    }
}
