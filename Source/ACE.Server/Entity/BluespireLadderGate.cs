namespace ACE.Server.Entity
{
    /// <summary>
    /// Why a Bluespire ladder portal let a character through, or refused. Returned by
    /// <see cref="BluespireLadderGate.Decide"/>.
    /// </summary>
    public enum BluespireLadderDecision
    {
        /// <summary>The character may enter this rung.</summary>
        Allow,

        /// <summary>
        /// The portal names a rung outside 1..<see cref="BluespireLadderGate.RungCount"/>. A content bug on
        /// the portal weenie: refuse rather than guess which rung was meant, because guessing low would let
        /// a typo open the whole ladder.
        /// </summary>
        MalformedRung,

        /// <summary>Rung 1 only: the character has never been stamped with the ladder's entry quest.</summary>
        MissingEntryQuest,

        /// <summary>The rung this one requires has never been cleared by this character.</summary>
        MissingPreviousRung
    }

    /// <summary>
    /// The whole Bluespire ladder entry gate as a PURE function over already-read values, so it can be unit
    /// tested without a live Player (ACE.Server.Tests can never build one - the Player constructor reaches
    /// DatabaseManager.Authentication). Deliberately the same shape, and for the same reason, as
    /// <see cref="OneTimeItemGrant"/>; PropertyManager reads throw in unit tests too, which is the second
    /// half of why nothing here consults it.
    ///
    /// NO LEVEL REQUIREMENT GATES ENTRY TO ANY DEPTH (owner ruling, 2026-09-24, round 19 A2): the per-rung
    /// bluespire_ladder_d{rung}_min_level tunables and the BelowLevel decision were removed entirely, not
    /// merely zeroed, so a future retune of those defaults cannot silently reintroduce the gate. Only the
    /// CLEAR of a rung is stamped (BluespireLadderD{k}Cleared); the permission to enter is recomputed from
    /// the remaining tunables (entry quest, previous-rung prerequisite) every time.
    ///
    /// Prerequisite evidence is "ever stamped" (QuestManager.HasQuest), deliberately NOT the
    /// hasQuest &amp;&amp; !CanSolve shape the built-in QuestRestriction path uses (Portal.cs). That shape
    /// would force max_Solves = 1 on the clear quests, and re-running a cleared rung has to stay allowed.
    /// </summary>
    public static class BluespireLadderGate
    {
        /// <summary>Rungs on the ladder. Rung numbers are 1-based, so the valid range is 1..RungCount.</summary>
        public const int RungCount = 6;

        /// <summary>True if <paramref name="rung"/> names a real rung of the ladder.</summary>
        public static bool IsValidRung(int rung)
        {
            return rung >= 1 && rung <= RungCount;
        }

        /// <summary>
        /// The sanitised prerequisite rung for <paramref name="rung"/>: the authored value when it names a
        /// STRICTLY LOWER real rung, and 0 (no rung prerequisite) otherwise.
        ///
        /// A value outside 1..(rung - 1) is a typo, and both directions of typo are dangerous in their own
        /// way: a value at or above the rung's own number would be unreachable and lock that rung shut
        /// forever, and a chain that pointed upward would form a cycle in which no rung could ever be the
        /// first one entered. Reading either as 0 degrades to "this rung has no rung prerequisite", which
        /// leaves the entry quest (for rung 1) still standing. The caller logs the substitution ONCE per
        /// rung so the typo is visible without flooding the log on every portal use.
        /// </summary>
        public static int SanitizeRequires(int rung, long requires)
        {
            if (requires < 1 || requires >= rung)
                return 0;

            return (int)requires;
        }

        /// <summary>
        /// The complete gate over already-read values.
        ///
        /// <paramref name="enabled"/> false returns <see cref="BluespireLadderDecision.Allow"/>, because the
        /// master switch is a KILL SWITCH and not a gate: switching the ladder off must make its portals
        /// behave as ordinary portals, never as sealed ones.
        ///
        /// Clause order is deliberate. A malformed rung is reported first because it is a content bug and is
        /// true regardless of anything else about the character. The entry quest is checked before the rung
        /// prerequisite so rung 1, the only rung with both, names the one a new character actually lacks.
        ///
        /// NO LEVEL ARGUMENT: there is no level requirement to enter any depth (owner ruling, 2026-09-24,
        /// round 19 A2). See the class remarks for why the min-level tunables and BelowLevel decision were
        /// removed rather than left in place and zeroed.
        /// </summary>
        /// <param name="rung">PropertyInt 9069 off the portal weenie</param>
        /// <param name="requiredRung">the ALREADY SANITISED prerequisite rung (see <see cref="SanitizeRequires"/>); 0 means none</param>
        /// <param name="previousRungCleared">QuestManager.HasQuest(ClearedQuestName(requiredRung)); ignored when requiredRung is 0</param>
        /// <param name="entryQuestName">bluespire_ladder_d1_entry_quest; blank turns the entry-quest requirement off</param>
        /// <param name="entryQuestHeld">QuestManager.HasQuest(entryQuestName); ignored unless rung is 1 and the name is non-blank</param>
        /// <param name="enabled">bluespire_ladder_enabled</param>
        public static BluespireLadderDecision Decide(int rung, int requiredRung,
            bool previousRungCleared, string entryQuestName, bool entryQuestHeld, bool enabled)
        {
            if (!enabled)
                return BluespireLadderDecision.Allow;

            if (!IsValidRung(rung))
                return BluespireLadderDecision.MalformedRung;

            if (rung == 1 && !string.IsNullOrWhiteSpace(entryQuestName) && !entryQuestHeld)
                return BluespireLadderDecision.MissingEntryQuest;

            if (requiredRung != 0 && !previousRungCleared)
                return BluespireLadderDecision.MissingPreviousRung;

            return BluespireLadderDecision.Allow;
        }

        /// <summary>
        /// The quest registry name stamped when a character clears <paramref name="rung"/>. A COMPILED
        /// CONSTANT rather than a tunable string: the payout table and the chain links both key off these
        /// names, and a live-editable name would let one typo silently detach a rung's reward from its
        /// clear with nothing to notice it. Returns null for a rung outside the ladder.
        /// </summary>
        public static string ClearedQuestName(int rung)
        {
            return IsValidRung(rung) ? "BluespireLadderD" + rung + "Cleared" : null;
        }

        /// <summary>
        /// The player-facing refusal for a non-Allow decision, so the wording lives beside the rule rather
        /// than at the call site. Returns null for <see cref="BluespireLadderDecision.Allow"/>.
        /// </summary>
        public static string RefusalMessage(BluespireLadderDecision decision, int rung, int requiredRung)
        {
            switch (decision)
            {
                case BluespireLadderDecision.MalformedRung:
                    return "This portal is misconfigured and will not open.";

                case BluespireLadderDecision.MissingEntryQuest:
                    return "The Bluespire is sealed to you. Seek out the first drum before you try these stairs.";

                case BluespireLadderDecision.MissingPreviousRung:
                    return $"The way down is barred. Clear the {OrdinalWord(requiredRung)} depth of the Bluespire first.";

                default:
                    return null;
            }
        }

        /// <summary>
        /// Ordinal words for the six rungs, so player-facing text reads as prose rather than as a row
        /// number. Falls back to a plain figure for anything outside the ladder, which the MalformedRung
        /// branch already makes unreachable from <see cref="RefusalMessage"/>. Shared with the payout
        /// message in BluespireLadderRewards so the two cannot drift.
        /// </summary>
        public static string OrdinalWord(int rung)
        {
            switch (rung)
            {
                case 1: return "first";
                case 2: return "second";
                case 3: return "third";
                case 4: return "fourth";
                case 5: return "fifth";
                case 6: return "sixth";
                default: return rung.ToString();
            }
        }
    }
}
