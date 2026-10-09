using System;
using System.Reflection;

using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The live half of the Bluespire ladder gate: it reads the bluespire_ladder_* tunables and hands them
    /// to the pure <see cref="BluespireLadderGate"/>. Every read happens on the portal use itself, so
    /// nothing about an unlock is ever baked into a stamp - only the CLEAR of a rung is stamped.
    ///
    /// NO LEVEL REQUIREMENT GATES ENTRY TO ANY DEPTH (owner ruling, 2026-09-24, round 19 A2): there is no
    /// MinLevel method here any more, on purpose - see BluespireLadderGate's remarks.
    ///
    /// Split from <see cref="BluespireLadderGate"/> on purpose: PropertyManager reads throw in unit tests,
    /// so the decision table is testable only while the tunable reads live somewhere else.
    /// </summary>
    public static class BluespireLadder
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// One latch per rung for the malformed-_requires warning, so a typo is reported once rather than on
        /// every portal use. Races here can only cost a duplicate log line, never a missed one, so it needs
        /// no lock.
        /// </summary>
        private static readonly bool[] requiresWarned = new bool[BluespireLadderGate.RungCount + 1];

        /// <summary>True while the ladder's gate and payouts are live. The master switch is a kill switch.</summary>
        public static bool Enabled => PropertyManager.GetBool("bluespire_ladder_enabled").Item;

        /// <summary>
        /// bluespire_ladder_d{rung}_requires, run through <see cref="BluespireLadderGate.SanitizeRequires"/>.
        /// A value outside 1..(rung - 1) is reported ONCE per rung and read as 0, so a typo can neither lock
        /// a rung shut forever nor point the chain upward into a cycle.
        /// </summary>
        public static int RequiredRung(int rung)
        {
            if (!BluespireLadderGate.IsValidRung(rung))
                return 0;

            var authored = PropertyManager.GetLong($"bluespire_ladder_d{rung}_requires").Item;
            var sanitized = BluespireLadderGate.SanitizeRequires(rung, authored);

            if (sanitized != authored && !requiresWarned[rung])
            {
                requiresWarned[rung] = true;

                log.Warn($"[BLUESPIRE] bluespire_ladder_d{rung}_requires is {authored}, which is outside 1..{rung - 1}; " +
                         "reading it as 0 (no rung prerequisite). This is reported once per rung per process.");
            }

            return sanitized;
        }

        /// <summary>bluespire_ladder_d1_entry_quest. Blank turns the rung 1 entry-quest requirement off.</summary>
        public static string EntryQuestName => PropertyManager.GetString("bluespire_ladder_d1_entry_quest").Item;

        /// <summary>
        /// The refusal text for this character at this rung, or null when they may pass. The ONE entry point
        /// Portal.CheckUseRequirements calls; everything below it is either a tunable read or the pure
        /// decision table.
        ///
        /// A null player or a rung the portal did not author cannot reach here - the caller has already
        /// established both - but a null QuestManager is guarded anyway, because a refusal that throws on a
        /// hot path would take the portal down rather than the character's entry.
        /// </summary>
        public static string CheckEntry(Player player, int rung)
        {
            if (player == null)
                return null;

            var enabled = Enabled;

            // Cheapest possible exit while the ladder is switched off: one bool read and no quest lookups.
            if (!enabled)
                return null;

            var requiredRung = RequiredRung(rung);
            var entryQuestName = EntryQuestName;

            var previousRungCleared = requiredRung != 0
                && HasQuest(player, BluespireLadderGate.ClearedQuestName(requiredRung));

            var entryQuestHeld = rung == 1
                && !string.IsNullOrWhiteSpace(entryQuestName)
                && HasQuest(player, entryQuestName);

            var decision = BluespireLadderGate.Decide(rung, requiredRung,
                previousRungCleared, entryQuestName, entryQuestHeld, enabled);

            return BluespireLadderGate.RefusalMessage(decision, rung, requiredRung);
        }

        /// <summary>
        /// "Ever stamped", cooldown-blind - QuestManager.HasQuest, deliberately not CanSolve. See the
        /// <see cref="BluespireLadderGate"/> remarks: CanSolve would force max_Solves = 1 on the clear
        /// quests, and re-running a cleared rung has to stay allowed.
        /// </summary>
        private static bool HasQuest(Player player, string questName)
        {
            if (string.IsNullOrWhiteSpace(questName))
                return false;

            try
            {
                return player.QuestManager != null && player.QuestManager.HasQuest(questName);
            }
            catch (Exception ex)
            {
                // Fail CLOSED: a lookup that threw has told us nothing, and "not held" is the reading that
                // cannot hand out content the character may not have earned. The cost of being wrong here is
                // one refused portal use on a server already logging an error.
                log.Error($"[BLUESPIRE] quest lookup '{questName}' threw for {player.Name}; treating it as not held", ex);
                return false;
            }
        }
    }
}
