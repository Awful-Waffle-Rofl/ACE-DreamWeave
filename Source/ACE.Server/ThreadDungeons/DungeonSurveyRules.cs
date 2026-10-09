using System;
using System.Collections.Generic;
using System.Globalization;

using ACE.Server.Managers;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Ledger seam for the daily-survey stamp hook (PHASE-2-DESIGN.md section 4.1). Isolates the pure
    /// window/count/total logic from QuestManager so it can be unit tested without a live Player.
    /// </summary>
    public interface ISurveyLedger
    {
        /// <summary>
        /// The row's LastTimeCompleted (unix seconds), or null if the row has never been stamped. This is
        /// what Record and Plan measure against a <see cref="SurveyDayClock"/> to decide whether the row was
        /// stamped on today's survey day - the daily-reset replacement for the old stamped-and-on-cooldown
        /// (Has AND NOT CanSolve) test.
        /// </summary>
        uint? LastCompleted(string quest);

        void Erase(string quest);
        void Stamp(string quest);
        void Increment(string quest);
        int Solves(string quest);
    }

    /// <summary>
    /// Which count band a turn-in fell into. Named for the three bands the Archivist speaks, which is what
    /// picks the band line: the enum is the shipped SurveyRewardMath.Tiers shape (1 / 5 / 10) and would need
    /// renaming if that table ever changed shape.
    /// </summary>
    public enum SurveyBand
    {
        /// <summary>Window expired, never opened, or nothing filed inside it. Nothing is payable.</summary>
        None,

        /// <summary>At least one survey but under the middle tier.</summary>
        Lt5,

        /// <summary>At or above the middle tier but under the top.</summary>
        Five,

        /// <summary>At or above the top tier.</summary>
        Ten,
    }

    /// <summary>
    /// The whole decision one Survey-Archivist visit makes, computed before anything is paid. Pure: it reads
    /// only the ledger and writes nothing, so every branch of the old 19-set emote cascade is unit testable.
    /// </summary>
    public sealed class SurveyTurnIn
    {
        /// <summary>The window row is stamped on today's survey day.</summary>
        public bool WindowLive { get; }

        /// <summary>Surveys filed in the current window. Meaningless (and never read) when the window is dead.</summary>
        public int Count { get; }

        public SurveyBand Band { get; }

        /// <summary>Tiers still unpaid this window that the count reaches, HIGHEST FIRST.</summary>
        public IReadOnlyList<int> TiersToPay { get; }

        /// <summary>The band was reached but every tier under it is already paid: the "come back tomorrow" line.</summary>
        public bool Held { get; }

        public SurveyTurnIn(bool windowLive, int count, SurveyBand band, IReadOnlyList<int> tiersToPay, bool held)
        {
            WindowLive = windowLive;
            Count = count;
            Band = band;
            TiersToPay = tiersToPay ?? Array.Empty<int>();
            Held = held;
        }

        public override string ToString()
            => $"window={(WindowLive ? "live" : "dead")} count={Count.ToString(CultureInfo.InvariantCulture)} band={Band} pay=[{string.Join(",", TiersToPay)}] held={Held}";
    }

    /// <summary>
    /// Pure rules for recording a filed survey (PHASE-2-DESIGN.md section 4.1). Deliberately reads no
    /// PropertyManager tunable: PropertyManager reads throw under the unit-test harness.
    /// </summary>
    public static class DungeonSurveyRules
    {
        public const string Window = "DynDungeonSurveyWindow";
        public const string Count = "DynDungeonSurveyCount";
        public const string Total = "DynDungeonSurveyTotal";

        /// <summary>Prefix of the per-tier paid latch: DynDungeonSurvey1 / 5 / 10.</summary>
        public const string SurveyQuestPrefix = "DynDungeonSurvey";

        /// <summary>The quest row that latches one tier as paid for the current window.</summary>
        public static string SurveyQuest(int tier) => SurveyQuestPrefix + tier.ToString(CultureInfo.InvariantCulture);

        public static bool Counts(int gemLevel, int minLevel) => gemLevel >= minLevel;

        /// <summary>
        /// Ruling P2-R22: a run whose plan placed nothing (spawned == 0) reaches Cleared with no clear to
        /// speak of - there was nothing to kill - so it must not file a survey either.
        /// </summary>
        public static bool Counts(int gemLevel, int minLevel, int spawned) => spawned > 0 && Counts(gemLevel, minLevel);

        /// <summary>
        /// Design 4.1 (daily reset, owner design 2026-09-17): if the window row is not stamped on today's
        /// survey day (never stamped, or last stamped on an earlier day) { Erase(Count); Stamp(Window); }
        /// Increment(Count); Increment(Total); returns the new Count (ledger.Solves(Count)).
        ///
        /// <paramref name="clock"/> is built by the caller from the instant the survey is actually filed -
        /// see the doc comment on <see cref="ThreadDungeonManager.RecordSurvey"/> for why that has to be read
        /// inside the enqueued delegate rather than passed in from outside it.
        /// </summary>
        public static int Record(ISurveyLedger ledger, SurveyDayClock clock)
        {
            var windowStamp = ledger.LastCompleted(Window);

            if (windowStamp == null || !clock.IsToday(windowStamp.Value))
            {
                ledger.Erase(Count);
                ledger.Stamp(Window);
            }

            ledger.Increment(Count);
            ledger.Increment(Total);

            return ledger.Solves(Count);
        }

        /// <summary>
        /// The Survey-Archivist turn-in decision, as one pure function over the ledger. This IS the old
        /// 19-set Use emote cascade, rewritten: WorldObject.OnActivate runs EmoteManager.OnUse BEFORE
        /// ActOnUse (WorldObject_Use.cs:177-186), so there is no seam that lets the emotes speak while C#
        /// computes a scaled award. The cascade was deleted from the weenie and reimplemented here.
        ///
        /// The five paper traces the deleted SQL header carried are the specification of this function and
        /// are pinned one-for-one in DungeonSurveyTurnInTests:
        ///   count 3, nothing paid      -> Lt5, pay {1}
        ///   count 6, tier 1 paid       -> Five, pay {5}                (tier 1 is NOT re-paid)
        ///   count 12, nothing paid     -> Ten, pay {10,5,1}
        ///   count 12, everything paid  -> Ten, pay {}, HELD
        ///   count 10, all paid, window from a previous day -> None, pay {}, not held (the stale count is
        ///     never read)
        ///
        /// Everything - the window, the count and all three tier latches - turns over together at ONE
        /// server-wide boundary (owner design 2026-09-17): midnight in <see cref="SurveyDayClock"/>'s zone,
        /// shifted by its reset hour. The window gate exists because DynDungeonSurveyCount is erased only
        /// when Record opens a NEW survey day, never merely by the day turning over on its own - Plan itself
        /// never erases anything - so a stale count from a previous day must not re-pay every tier below it.
        ///
        /// Reads nothing outside the ledger and the clock, and writes nothing: the caller pays and stamps.
        /// </summary>
        public static SurveyTurnIn Plan(ISurveyLedger ledger, SurveyDayClock clock)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            if (clock == null) throw new ArgumentNullException(nameof(clock));

            // Stamped AND on today's survey day = the window is still live. The daily-reset replacement for
            // the old stamped-and-on-cooldown (Has AND NOT CanSolve) test the deleted emote set 1 made.
            var windowStamp = ledger.LastCompleted(Window);
            var windowLive = windowStamp != null && clock.IsToday(windowStamp.Value);

            if (!windowLive)
                return new SurveyTurnIn(false, 0, SurveyBand.None, Array.Empty<int>(), false);

            var count = ledger.Solves(Count);

            var band = SurveyBand.None;
            var candidates = new List<int>();

            // Tiers highest first: every tier the count reaches is a candidate, so one visit pays every
            // unpaid tier it passes (the cascade's 10 -> 5 -> 1 walk).
            var tiers = SurveyRewardMath.Tiers;
            for (var i = tiers.Count - 1; i >= 0; i--)
            {
                if (count < tiers[i].Surveys)
                    continue;

                if (candidates.Count == 0)
                    band = BandForTierIndex(i);

                candidates.Add(tiers[i].Surveys);
            }

            if (band == SurveyBand.None)
                return new SurveyTurnIn(true, count, SurveyBand.None, Array.Empty<int>(), false);

            var toPay = new List<int>();
            foreach (var tier in candidates)
            {
                var quest = SurveyQuest(tier);

                // Same test again: stamped on today's survey day means already paid today. Anything else -
                // never stamped, or stamped on an earlier day - is unpaid.
                var stamp = ledger.LastCompleted(quest);
                var paid = stamp != null && clock.IsToday(stamp.Value);

                if (!paid)
                    toPay.Add(tier);
            }

            return new SurveyTurnIn(true, count, band, toPay, toPay.Count == 0);
        }

        /// <summary>
        /// Maps a position in SurveyRewardMath.Tiers onto the band the Archivist speaks. Index-based rather
        /// than value-based so the tier numbers stay in exactly one declaration (SurveyRewardMath.Tiers);
        /// a table with a different NUMBER of tiers would need these names revisited.
        /// </summary>
        private static SurveyBand BandForTierIndex(int index)
        {
            switch (index)
            {
                case 0: return SurveyBand.Lt5;
                case 1: return SurveyBand.Five;
                default: return SurveyBand.Ten;
            }
        }
    }

    /// <summary>
    /// Wraps a Player's QuestManager as an ISurveyLedger for production use.
    /// </summary>
    internal sealed class QuestManagerSurveyLedger : ISurveyLedger
    {
        private readonly QuestManager questManager;

        public QuestManagerSurveyLedger(QuestManager questManager)
        {
            this.questManager = questManager;
        }

        public uint? LastCompleted(string quest) => questManager.GetQuest(quest)?.LastTimeCompleted;

        public void Erase(string quest) => questManager.Erase(quest);

        public void Stamp(string quest) => questManager.Stamp(quest);

        public void Increment(string quest) => questManager.Increment(quest);

        public int Solves(string quest) => questManager.GetCurrentSolves(quest);
    }
}
