using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACE.Server.Pvp
{
    /// <summary>The per-character state slots the stateful dailies keep, each in its own reserved PropertyString.</summary>
    public enum PvpDailyStateSlot
    {
        /// <summary>ProvingGround: the opponent accounts defeated while the counter quest is held (PropertyString 9026).</summary>
        ProvingGround,

        /// <summary>GladiatorsStreak: the opponent account of the streak's last win (PropertyString 9027).</summary>
        GladiatorsStreak,
    }

    /// <summary>The per-character lifetime stamps the PvP match facts keep for a later vendor purchase gate (WP-H).</summary>
    public enum PvpDailyStamp
    {
        /// <summary>Lifetime battleground wins, King of the Hill and Attack/Defend (PropertyInt 9090).</summary>
        LifetimeBattlegroundWins,

        /// <summary>The highest PvP rating Peak ever seen across every ladder (PropertyInt 9091).</summary>
        PeakRating,
    }

    /// <summary>
    /// What <see cref="PvpDailyProgress"/> needs from a character. The live one is the Player (Player_PvpDailies.cs): the quest calls go
    /// to its QuestManager (HasQuest, GetCurrentSolves, SetQuestCompletions) and the state and stamps to its reserved properties. Tests use
    /// a dictionary-backed fake.
    /// </summary>
    public interface IPvpDailyTarget
    {
        bool HasQuest(string questName);

        int GetSolves(string questName);

        void SetSolves(string questName, int completions);

        /// <summary>The slot's stored state, or null when absent.</summary>
        string GetState(PvpDailyStateSlot slot);

        /// <summary>Stores the slot's state; null removes it.</summary>
        void SetState(PvpDailyStateSlot slot, string value);

        int GetStamp(PvpDailyStamp stamp);

        void SetStamp(PvpDailyStamp stamp, int value);
    }

    /// <summary>
    /// The stored state of a stateful daily: the counter value it was written with, and the opponent accounts (ProvingGround: every one
    /// defeated; GladiatorsStreak: the last win's, at most one). Bound to the counter: a state whose <see cref="Count"/> does not match the
    /// quest's current solves belongs to an earlier acceptance of the daily (the counter was reset or re-created) and is read as empty.
    /// Text form <c>v1|&lt;count&gt;|&lt;hex&gt;,&lt;hex&gt;</c>. Bounded: text over <see cref="MaxTextLength"/> characters or with more than
    /// <see cref="MaxAccounts"/> accounts is rejected as empty, and <see cref="Append"/> keeps only the newest <see cref="KeepAccounts"/>, which
    /// always formats within the length limit. Pure.
    /// </summary>
    public sealed record PvpDailyOpponentState(int Count, IReadOnlyList<uint> Accounts)
    {
        private const string Version = "v1";

        /// <summary>The longest stored text that is parsed at all.</summary>
        public const int MaxTextLength = 128;

        /// <summary>The most accounts a parsed state may carry.</summary>
        public const int MaxAccounts = 16;

        /// <summary>
        /// The accounts kept on append (the newest). Lower than <see cref="MaxAccounts"/> because 16 accounts of 8 hex digits do not fit in
        /// <see cref="MaxTextLength"/>: 12 always do (12 * 9 - 1 = 107 characters plus a prefix of at most 14).
        /// </summary>
        public const int KeepAccounts = 12;

        public static PvpDailyOpponentState Empty(int count) => new PvpDailyOpponentState(count, Array.Empty<uint>());

        /// <summary>The stored state when it parses AND belongs to <paramref name="currentSolves"/>; otherwise empty at that count.</summary>
        public static PvpDailyOpponentState Parse(string text, int currentSolves)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
                return Empty(currentSolves);

            var parts = text.Split('|');

            if (parts.Length != 3 || parts[0] != Version || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count != currentSolves)
                return Empty(currentSolves);

            var tokens = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length > MaxAccounts)
                return Empty(currentSolves);

            var accounts = new List<uint>();

            foreach (var token in tokens)
            {
                if (!uint.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var account))
                    return Empty(currentSolves);

                accounts.Add(account);
            }

            return new PvpDailyOpponentState(count, accounts);
        }

        /// <summary>This state with <paramref name="account"/> appended at <paramref name="count"/>, keeping only the newest <see cref="KeepAccounts"/>.</summary>
        public PvpDailyOpponentState Append(uint account, int count)
        {
            var all = Accounts.Append(account).ToList();

            return new PvpDailyOpponentState(count, all.Skip(Math.Max(0, all.Count - KeepAccounts)).ToList());
        }

        public string Format() => string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}", Version, Count, string.Join(",", Accounts.Select(a => a.ToString("X8", CultureInfo.InvariantCulture))));
    }

    /// <summary>One counter that advanced: its task and the progress line <see cref="PvpDailyTaskDef.ProgressLine"/> built for it.</summary>
    public sealed record PvpDailyMove(PvpDailyTaskDef Task, string Line);

    /// <summary>
    /// What one <see cref="PvpDailyProgress.Apply"/> did: <see cref="Log"/>, one line per change for the server log, and
    /// <see cref="Messages"/>, the player's chat lines - one per counter that advanced, the progress line below the target and the
    /// completion line at it (<see cref="PvpDailyTaskDef.ProgressLine"/>). A streak reset and the stamps add log lines only.
    /// </summary>
    public sealed class PvpDailyProgressResult
    {
        private readonly List<string> log = new();
        private readonly List<string> messages = new();
        private readonly List<PvpDailyMove> moved = new();

        public IReadOnlyList<string> Log => log;

        public IReadOnlyList<string> Messages => messages;

        /// <summary>The same lines as <see cref="Messages"/>, in the same order, each with the task that produced it.</summary>
        public IReadOnlyList<PvpDailyMove> Moved => moved;

        /// <summary>Number of log lines: 0 means nothing changed.</summary>
        public int Count => log.Count;

        public void Add(string logLine) => log.Add(logLine);

        public void Progressed(PvpDailyTaskDef task, int count)
        {
            var line = task.ProgressLine(count);

            messages.Add(line);
            moved.Add(new PvpDailyMove(task, line));
        }
    }

    /// <summary>
    /// Applies one seat's <see cref="PvpMatchFacts"/> to a character, on that character's own action chain. For every task in
    /// <see cref="PvpDailyCatalog"/>: the counter moves ONLY while the character holds the counter quest (HasQuest: they accepted the daily)
    /// and only while it is below the target, by SetQuestCompletions(min(current + delta, target)). No KillQuest properties and no parallel
    /// ledger. The stateful tasks keep their opponent state only while the quest is held and drop it when it is absent. The two lifetime
    /// stamps (battleground wins, peak rating) are written whatever quests are held. Returns the log lines and the player's chat lines.
    /// </summary>
    public static class PvpDailyProgress
    {
        public static PvpDailyProgressResult Apply(IPvpDailyTarget target, PvpMatchFacts facts)
        {
            var changes = new PvpDailyProgressResult();

            if (target == null || facts == null)
                return changes;

            // A forfeit fact (a Live 1v1 forfeit) is a loss for GladiatorsStreak ONLY: it resets the streak and advances nothing else,
            // no counter and no stamp. Every other task skips it.
            if (facts.Forfeited)
            {
                if (facts.Mode == PvpDailyMode.OneVOne)
                    ApplyStreak(target, PvpDailyCatalog.Get(PvpDailyTask.GladiatorsStreak), facts, changes);

                return changes;
            }

            foreach (var task in PvpDailyCatalog.Tasks)
            {
                switch (task.Kind)
                {
                    case PvpDailyProgressKind.DistinctOpponents:
                        ApplyDistinct(target, task, facts, changes);
                        break;

                    case PvpDailyProgressKind.Streak:
                        ApplyStreak(target, task, facts, changes);
                        break;

                    default:
                        Add(target, task, task.Delta(facts), changes);
                        break;
                }
            }

            ApplyStamps(target, facts, changes);

            return changes;
        }

        /// <summary>
        /// THE progress primitive: adds <paramref name="delta"/> to the task's counter only when the quest is held and below the task's
        /// target, clamped to the target. Returns true when it wrote; <paramref name="changes"/> then gets the log line and the player's line.
        /// </summary>
        public static bool Add(IPvpDailyTarget t, PvpDailyTaskDef task, long delta, PvpDailyProgressResult changes = null)
        {
            var counter = task.CounterName;
            var target = task.Target;

            if (delta <= 0 || !t.HasQuest(counter))
                return false;

            var current = t.GetSolves(counter);

            if (current >= target)
                return false;

            // Compared against the room left, never summed: a cumulative fact can be as large as long.MaxValue.
            var next = delta >= (long)target - current ? target : current + (int)delta;

            var stored = WriteSolves(t, counter, next);
            changes?.Add($"{counter} {current}->{next}{ShortNote(stored, next)}");

            if (stored >= next)
                changes?.Progressed(task, stored);

            return true;
        }

        /// <summary>
        /// Writes a counter and returns what the target STORED, read back: the live write can fall short of <paramref name="next"/> (the
        /// quest row's max_solves clamp, a mule, a templated player). Every player line is built from this stored value, and a write that fell
        /// short sends no line at all, so the chat never claims progress the quest registry does not hold.
        /// </summary>
        private static int WriteSolves(IPvpDailyTarget t, string counter, int next)
        {
            t.SetSolves(counter, next);
            return t.GetSolves(counter);
        }

        /// <summary>The log suffix for a write that stored less than it wrote; empty otherwise.</summary>
        private static string ShortNote(int stored, int next) => stored == next ? string.Empty : $" (stored {stored}; no player line)";

        private static void ApplyDistinct(IPvpDailyTarget t, PvpDailyTaskDef task, PvpMatchFacts facts, PvpDailyProgressResult changes)
        {
            var counter = task.CounterName;

            if (!t.HasQuest(counter))
            {
                ClearState(t, PvpDailyStateSlot.ProvingGround);
                return;
            }

            if (task.Delta(facts) <= 0)
                return;

            var current = t.GetSolves(counter);

            if (current >= task.Target)
                return;

            var opponent = facts.OpponentAccount.Value;
            var state = PvpDailyOpponentState.Parse(t.GetState(PvpDailyStateSlot.ProvingGround), current);

            if (state.Accounts.Contains(opponent))
                return; // already defeated while this quest is held: not a new opponent

            var next = Math.Min(current + 1, task.Target);

            var stored = WriteSolves(t, counter, next);
            t.SetState(PvpDailyStateSlot.ProvingGround, state.Append(opponent, next).Format());
            changes.Add($"{counter} {current}->{next} (new opponent account {opponent:X8}){ShortNote(stored, next)}");

            if (stored >= next)
                changes.Progressed(task, stored);
        }

        private static void ApplyStreak(IPvpDailyTarget t, PvpDailyTaskDef task, PvpMatchFacts facts, PvpDailyProgressResult changes)
        {
            var counter = task.CounterName;

            if (!t.HasQuest(counter))
            {
                ClearState(t, PvpDailyStateSlot.GladiatorsStreak);
                return;
            }

            if (facts.Mode != PvpDailyMode.OneVOne)
                return;

            var current = t.GetSolves(counter);

            // Reached: the streak is done for this acceptance, and a later loss cannot take it back.
            if (current >= task.Target)
                return;

            var state = PvpDailyOpponentState.Parse(t.GetState(PvpDailyStateSlot.GladiatorsStreak), current);

            // Branch on the RESULT first: only a loss, a draw or a forfeit may break the streak. A win whose opponent account is unknown
            // cannot be judged against the previous win, so it holds the streak where it is rather than resetting it.
            if (facts.Won && !facts.Forfeited)
            {
                if (!facts.OpponentAccount.HasValue)
                    return;

                var opponent = facts.OpponentAccount.Value;

                if (state.Accounts.Count > 0 && state.Accounts[state.Accounts.Count - 1] == opponent)
                    return; // the same opponent as the previous win: the streak stays where it is

                var next = Math.Min(current + 1, task.Target);

                var stored = WriteSolves(t, counter, next);
                t.SetState(PvpDailyStateSlot.GladiatorsStreak, new PvpDailyOpponentState(next, new[] { opponent }).Format());
                changes.Add($"{counter} {current}->{next} (streak, opponent account {opponent:X8}){ShortNote(stored, next)}");

                if (stored >= next)
                    changes.Progressed(task, stored);
                return;
            }

            // A 1v1 loss or draw breaks the streak, and so does a forfeit (a forfeit fact is always a loss).
            if (current != 0)
            {
                t.SetSolves(counter, 0);
                changes.Add($"{counter} {current}->0 (streak broken by a {(facts.Forfeited ? "forfeit" : facts.Result.ToString().ToLowerInvariant())})");
            }

            if (t.GetState(PvpDailyStateSlot.GladiatorsStreak) != null)
                t.SetState(PvpDailyStateSlot.GladiatorsStreak, PvpDailyOpponentState.Empty(0).Format());
        }

        /// <summary>
        /// The GladiatorsStreak state value that means "a Live 1v1 forfeit happened while this character was offline or logging out; reset
        /// the streak at the next login". Written on the stored character by <see cref="PvpDailyForfeitDispatcher"/> because an offline
        /// character has no quest registry to write; never a valid opponent state (Parse reads it as empty).
        /// </summary>
        public const string PendingStreakResetMarker = "v1|pending-forfeit-reset|";

        /// <summary>
        /// Login: true when the streak state carries <see cref="PendingStreakResetMarker"/>, which is removed here. The caller then applies a
        /// forfeit fact (PvpMatchFacts.ForfeitOf) through the normal path, which resets the streak if the quest is held and not yet complete.
        /// </summary>
        public static bool TakePendingStreakReset(IPvpDailyTarget t)
        {
            if (t == null || t.GetState(PvpDailyStateSlot.GladiatorsStreak) != PendingStreakResetMarker)
                return false;

            t.SetState(PvpDailyStateSlot.GladiatorsStreak, null);
            return true;
        }

        /// <summary>
        /// Login: takes a pending GladiatorsStreak reset (<see cref="TakePendingStreakReset"/>) and, when one was pending, hands
        /// <paramref name="apply"/> the forfeit fact for <paramref name="characterId"/> (<see cref="PvpMatchFacts.ForfeitOf"/>). The live
        /// caller passes Player.ApplyPvpDailyFacts, so the template-lock bypass stays scoped to that one method. True when a reset was pending.
        /// </summary>
        public static bool ApplyPendingStreakReset(IPvpDailyTarget t, uint characterId, Action<PvpMatchFacts> apply)
        {
            if (!TakePendingStreakReset(t))
                return false;

            apply(PvpMatchFacts.ForfeitOf(Guid.Empty, characterId, 0));
            return true;
        }

        private static void ClearState(IPvpDailyTarget t, PvpDailyStateSlot slot)
        {
            if (t.GetState(slot) != null)
                t.SetState(slot, null);
        }

        private static void ApplyStamps(IPvpDailyTarget t, PvpMatchFacts facts, PvpDailyProgressResult changes)
        {
            if (facts.IsBattleground && facts.Won)
            {
                var wins = t.GetStamp(PvpDailyStamp.LifetimeBattlegroundWins);
                var next = wins == int.MaxValue ? wins : wins + 1;

                t.SetStamp(PvpDailyStamp.LifetimeBattlegroundWins, next);
                changes.Add($"lifetime battleground wins {wins}->{next}");
            }

            var peak = t.GetStamp(PvpDailyStamp.PeakRating);

            if (facts.PeakRating > peak)
            {
                t.SetStamp(PvpDailyStamp.PeakRating, facts.PeakRating);
                changes.Add($"peak rating {peak}->{facts.PeakRating}");
            }
        }
    }
}
