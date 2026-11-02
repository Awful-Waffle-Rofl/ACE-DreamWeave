using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp
{
    /// <summary>The PvP daily and weekly tasks (owner spec 2026-10-15). The enum name is the task name in every quest row name.</summary>
    public enum PvpDailyTask
    {
        WarmongersDue,
        WeeklyBattlegrounds,
        BloodInTheSand,
        FirstBlood,
        ProvingGround,
        GladiatorsStreak,
        IntoThePit,
        PitVictor,
        Carnage,
        Bloodbath,
        HoldTheCrown,
        KingsGround,
        Usurper,
        LastOneStanding,
        SiegeDuty,
        CrystalBreaker,
        Bulwark,
        Shatterpoint,
        Holdfast,
    }

    /// <summary>How a task's counter moves.</summary>
    public enum PvpDailyProgressKind
    {
        /// <summary>The counter goes up by the task's delta.</summary>
        Additive,

        /// <summary>
        /// ProvingGround: a 1v1 win advances the counter only against an opponent ACCOUNT not yet defeated while this quest is held
        /// (per-character state in PropertyString.PvpDailyProvingGroundState). The delta is the candidate signal, not the step.
        /// </summary>
        DistinctOpponents,

        /// <summary>
        /// GladiatorsStreak: consecutive 1v1 wins, each against a different opponent account from the previous win. A loss or draw resets
        /// the counter to 0; a win against the previous win's opponent leaves it where it is (per-character state in
        /// PropertyString.PvpDailyGladiatorsStreakState). The delta is the candidate signal, not the step.
        /// </summary>
        Streak,
    }

    /// <summary>
    /// One task: the quest-registry counter it advances (<see cref="CounterName"/>), the completion flag the content PR stamps
    /// (<see cref="DoneName"/>; not used by the server), its target, the fact-to-delta function, and what the player sees: its
    /// <see cref="DisplayName"/>, the NPC who gives it (<see cref="GiverName"/>) and the <see cref="Unit"/> printed after the target.
    /// </summary>
    public sealed record PvpDailyTaskDef(PvpDailyTask Task, int Target, bool Weekly, PvpDailyProgressKind Kind, Func<PvpMatchFacts, long> Delta,
        string DisplayName, string GiverName, string Unit = "")
    {
        public string CounterName => PvpDailyCatalog.CounterNameOf(Task);

        public string DoneName => PvpDailyCatalog.DoneNameOf(Task);

        /// <summary>The chat line after the counter moved to <paramref name="count"/>: progress below the target, the completion line at it.</summary>
        public string ProgressLine(int count) => count >= Target
            ? $"[Arena] {DisplayName} is complete. Return to {GiverName}."
            : $"[Arena] {DisplayName}: {count} of {Target}{Unit}.";
    }

    /// <summary>
    /// THE single source of truth for the PvP daily and weekly tasks: each task's counter name, target and fact-to-delta function. The
    /// quest-content PR (quest rows, NPC emotes) reads <see cref="PvpDailyTaskDef.CounterName"/> and <see cref="PvpDailyTaskDef.DoneName"/>
    /// byte for byte: counters are <c>PvpDaily&lt;Task&gt;Count</c> and done flags <c>PvpDaily&lt;Task&gt;Done</c>, the weekly included.
    /// Repeat timers (20 h daily, 7 days weekly) belong to the quest rows, not to this catalog; <see cref="PvpDailyTaskDef.Weekly"/> only
    /// records which one a task is. Every delta assumes the facts' seat was ELIGIBLE (a facts record exists only for one), so "completed"
    /// is simply "this mode". Pure and static.
    ///
    /// <para/>
    /// CONTENT REQUIREMENT: every <c>PvpDaily&lt;Task&gt;Count</c> counter needs a world <c>quest</c> row with <c>max_solves</c> of -1 or
    /// at least the task's <see cref="PvpDailyTaskDef.Target"/>. QuestManager.SetQuestCompletions clamps every write to that row's
    /// max_solves, and a MISSING row reads as 0, so without it the counter never moves (the player target logs a warning once per counter
    /// name when a write does not read back).
    /// </summary>
    public static partial class PvpDailyCatalog
    {
        public const string NamePrefix = "PvpDaily";

        public const string CounterSuffix = "Count";

        public const string DoneSuffix = "Done";

        /// <summary>Bloodbath: the kills in ONE Tugak Brawl match that count it (once).</summary>
        public const int BloodbathKillsInOneMatch = 3;

        public static string CounterNameOf(PvpDailyTask task) => NamePrefix + task + CounterSuffix;

        public static string DoneNameOf(PvpDailyTask task) => NamePrefix + task + DoneSuffix;

        private static long Once(bool condition) => condition ? 1 : 0;

        /// <summary>The giver of Warmonger's Due, The Long War and the five Attack/Defend tasks.</summary>
        public const string Vidorian = "Dark Master Vidorian";

        /// <summary>The giver of the 1v1 and Tugak Brawl tasks.</summary>
        public const string Ferah = "Black Ferah";

        /// <summary>The giver of the King of the Hill tasks.</summary>
        public const string LerRhan = "Ler Rhan";

        public static IReadOnlyList<PvpDailyTaskDef> Tasks { get; } = new List<PvpDailyTaskDef>
        {
            // Battlegrounds (King of the Hill or Attack/Defend)
            new(PvpDailyTask.WarmongersDue,       5,    false, PvpDailyProgressKind.Additive, f => Once(f.IsBattleground), "Warmonger's Due", Vidorian),
            new(PvpDailyTask.WeeklyBattlegrounds, 20,   true,  PvpDailyProgressKind.Additive, f => Once(f.IsBattleground), "The Long War", Vidorian),

            // 1v1
            new(PvpDailyTask.BloodInTheSand,      3,    false, PvpDailyProgressKind.Additive, f => Once(f.Mode == PvpDailyMode.OneVOne), "Blood in the Sand", Ferah),
            new(PvpDailyTask.FirstBlood,          1,    false, PvpDailyProgressKind.Additive, f => Once(f.Mode == PvpDailyMode.OneVOne && f.Won), "First Blood", Ferah),
            new(PvpDailyTask.ProvingGround,       3,    false, PvpDailyProgressKind.DistinctOpponents, f => Once(f.Mode == PvpDailyMode.OneVOne && f.Won && f.OpponentAccount.HasValue), "Proving Ground", Ferah),
            new(PvpDailyTask.GladiatorsStreak,    2,    false, PvpDailyProgressKind.Streak, f => Once(f.Mode == PvpDailyMode.OneVOne && f.Won && f.OpponentAccount.HasValue), "Gladiator's Streak", Ferah),

            // Tugak Brawl
            new(PvpDailyTask.IntoThePit,          3,    false, PvpDailyProgressKind.Additive, f => Once(f.Mode == PvpDailyMode.Tugak), "Into the Pit", Ferah),
            new(PvpDailyTask.PitVictor,           1,    false, PvpDailyProgressKind.Additive, f => Once(f.Mode == PvpDailyMode.Tugak && f.Won), "Pit Victor", Ferah),
            new(PvpDailyTask.Carnage,             5,    false, PvpDailyProgressKind.Additive, f => f.Mode == PvpDailyMode.Tugak ? Math.Max(0, f.Kills) : 0, "Carnage", Ferah),
            new(PvpDailyTask.Bloodbath,           1,    false, PvpDailyProgressKind.Additive, f => Once(f.Mode == PvpDailyMode.Tugak && f.Kills >= BloodbathKillsInOneMatch), "Bloodbath", Ferah),

            // King of the Hill
            new(PvpDailyTask.HoldTheCrown,        2,    false, PvpDailyProgressKind.Additive, f => Once(f.Mode == PvpDailyMode.Koth), "Hold the Crown", LerRhan),
            new(PvpDailyTask.KingsGround,         180,  false, PvpDailyProgressKind.Additive, f => f.Mode == PvpDailyMode.Koth ? Math.Max(0, f.HillSeconds) : 0, "King's Ground", LerRhan, " seconds"),
            new(PvpDailyTask.Usurper,             5,    false, PvpDailyProgressKind.Additive, f => f.Mode == PvpDailyMode.Koth ? Math.Max(0, f.KillshotsOnHill) : 0, "Usurper", LerRhan),
            new(PvpDailyTask.LastOneStanding,     1,    false, PvpDailyProgressKind.Additive, f => Once(f.Mode == PvpDailyMode.Koth && f.Won), "Last One Standing", LerRhan),

            // Attack/Defend (team 0 attacks, team 1 defends)
            new(PvpDailyTask.SiegeDuty,           2,    false, PvpDailyProgressKind.Additive, f => Once(f.Mode == PvpDailyMode.AttackDefend), "Siege Duty", Vidorian),
            new(PvpDailyTask.CrystalBreaker,      5000, false, PvpDailyProgressKind.Additive, f => f.Mode == PvpDailyMode.AttackDefend ? Math.Max(0, f.CrystalDamage) : 0, "Crystal Breaker", Vidorian, " damage"),
            new(PvpDailyTask.Bulwark,             5,    false, PvpDailyProgressKind.Additive, f => f.IsDefender ? Math.Max(0, f.KillshotsNearCrystal) : 0, "Bulwark", Vidorian),
            new(PvpDailyTask.Shatterpoint,        1,    false, PvpDailyProgressKind.Additive, f => Once(f.IsAttacker && f.Won), "Shatterpoint", Vidorian),
            new(PvpDailyTask.Holdfast,            1,    false, PvpDailyProgressKind.Additive, f => Once(f.IsDefender && f.Won), "Holdfast", Vidorian),
        };

        private static readonly Dictionary<PvpDailyTask, PvpDailyTaskDef> byTask = Tasks.ToDictionary(t => t.Task);

        public static PvpDailyTaskDef Get(PvpDailyTask task) => byTask[task];
    }
}
