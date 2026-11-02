using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common.Extensions;

namespace ACE.Server.Pvp
{
    /// <summary>What <see cref="PvpDailyCatalog.StatusLines"/> reads from a character. The live one is the Player (Player_PvpDailies.cs); tests use a fake.</summary>
    public interface IPvpDailyStatusSource
    {
        /// <summary>True when the character holds the quest (the counter quest while a daily is in progress).</summary>
        bool HasQuest(string questName);

        /// <summary>The counter quest's current solves.</summary>
        int GetSolves(string questName);

        /// <summary>
        /// The time left before the turn-in flag quest (<see cref="PvpDailyTaskDef.DoneName"/>) can be solved again, or null when the
        /// character is not on cooldown for it (never turned in, the timer already ran out, or no usable timer).
        /// </summary>
        TimeSpan? CooldownRemaining(string questName);
    }

    public static partial class PvpDailyCatalog
    {
        public const string StatusHeader = "[Arena] Your dailies:";

        public const string StatusHeaderOthers = "[Arena] Your other dailies:";

        /// <summary>The chat line for a task on turn-in cooldown.</summary>
        public static string CooldownLine(PvpDailyTaskDef task, TimeSpan remaining)
            => $"[Arena] {task.DisplayName}: done, available again in {remaining.GetFriendlyString()}.";

        /// <summary>
        /// The "this match" lines to send after a match: each moved line EXCEPT those of a task on turn-in cooldown. A cooling task's
        /// counter is re-armed by the pay chain and keeps advancing, but its giver refuses it until the cooldown ends, so "X is
        /// complete. Return to Y." would be false; its status line ("done, available again in t. Next: n of target." or "Next: already
        /// complete.") carries the progress instead. The result is also exactly the <c>alreadyShown</c> set to pass to
        /// <see cref="StatusLines"/>. Pure.
        /// </summary>
        public static IReadOnlyList<string> MatchLines(IEnumerable<PvpDailyMove> moved, IPvpDailyStatusSource source)
            => moved == null ? new List<string>() : moved.Where(m => !TryCooldown(source, m.Task, out _)).Select(m => m.Line).ToList();

        /// <summary>
        /// True when the task is on turn-in cooldown, with the time left. Reads <see cref="IPvpDailyStatusSource.CooldownRemaining"/>
        /// exactly ONCE: the live source reads the wall clock, so a second read could see the cooldown already over.
        /// </summary>
        private static bool TryCooldown(IPvpDailyStatusSource source, PvpDailyTaskDef task, out TimeSpan remaining)
        {
            remaining = TimeSpan.Zero;

            if (source == null || !(source.CooldownRemaining(task.DoneName) is TimeSpan left) || left <= TimeSpan.Zero)
                return false;

            remaining = left;
            return true;
        }

        /// <summary>
        /// The raw <c>QuestManager.GetNextSolveTime</c> value as a countdown to show: null for MinValue (can solve now / never done),
        /// MaxValue (max solves reached or no world row) and any zero or negative span; otherwise the span itself. Pure.
        /// </summary>
        public static TimeSpan? ToCountdown(TimeSpan raw) => raw > TimeSpan.Zero && raw < TimeSpan.MaxValue ? raw : null;

        /// <summary>
        /// The canonical <see cref="PvpDailyTaskDef.GiverName"/> for <paramref name="giver"/> (case-insensitive), or null when no task
        /// has that giver.
        /// </summary>
        public static string CanonicalGiver(string giver)
            => giver == null ? null : Tasks.Select(t => t.GiverName).FirstOrDefault(g => string.Equals(g, giver, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The player's daily standing, in catalog order. The turn-in cooldown is evaluated FIRST, because every pay chain re-arms the
        /// counter quest at 0 right after stamping the Done flag (so the counter is "held" all through the cooldown): a task on cooldown
        /// prints its cooldown line, with " Next: n of target." appended when the re-armed counter is above 0 (" Next: already
        /// complete." at the target). A task not on cooldown but held prints its <see cref="PvpDailyTaskDef.ProgressLine"/> (the
        /// "Return to" line at the target). Tasks neither held nor cooling are omitted. An unknown <paramref name="giver"/> yields
        /// nothing; a known one is matched case-insensitively and limits it to that NPC's tasks (see <see cref="CanonicalGiver"/>). With no <paramref name="alreadyShown"/> there is a header, and
        /// when nothing qualifies a single line says no dailies are held and names the giver(s). With <paramref name="alreadyShown"/> (the
        /// lines just sent, e.g. the moved-counter lines after a match; may be empty) a held, non-cooling task whose progress line was
        /// just sent is dropped; a cooling task is never dropped (its cooldown line is new information) but loses its " Next:" part when
        /// its progress line was just sent, so the progress is not told twice. The header reads "other dailies" when any lines were
        /// shown, and nothing at all is returned when nothing else qualifies. Pure.
        /// </summary>
        public static IReadOnlyList<string> StatusLines(IPvpDailyStatusSource source, string giver = null, IReadOnlyCollection<string> alreadyShown = null)
        {
            var lines = new List<string>();

            if (giver != null)
            {
                giver = CanonicalGiver(giver);

                if (giver == null)
                    return lines;
            }

            if (source != null)
            {
                foreach (var task in Tasks)
                {
                    if (giver != null && task.GiverName != giver)
                        continue;

                    var held = source.HasQuest(task.CounterName);
                    var solves = held ? source.GetSolves(task.CounterName) : 0;
                    var progress = held ? task.ProgressLine(solves) : null;
                    var shown = progress != null && alreadyShown != null && alreadyShown.Contains(progress);
                    string line = null;

                    if (TryCooldown(source, task, out var remaining))
                    {
                        line = CooldownLine(task, remaining);

                        if (held && solves > 0 && !shown)
                            line += solves >= task.Target ? " Next: already complete." : $" Next: {solves} of {task.Target}{task.Unit}.";
                    }
                    else if (held && !shown)
                    {
                        line = progress;
                    }

                    if (line != null)
                        lines.Add(line);
                }
            }

            var followUp = alreadyShown != null;

            if (lines.Count > 0)
            {
                lines.Insert(0, followUp && alreadyShown.Count > 0 ? StatusHeaderOthers : StatusHeader);
                return lines;
            }

            if (followUp)
                return lines;

            var givers = giver != null ? giver : $"{Vidorian}, {Ferah} or {LerRhan}";

            return new[] { $"[Arena] You hold no PvP dailies. Ask {givers} for one." };
        }
    }
}
