using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Knots
{
    /// <summary>One item the exchanger considers: the fields of a held item that decide whether it is a bare Pattern and for which pinnacle.</summary>
    /// <remarks>Traded is true when the item is in the player's open trade window, or sits inside a side pack that is.</remarks>
    public readonly record struct PatternCandidate(uint Guid, int Kind, int FiberCount, string Key, bool Traded = false);

    /// <summary>The outcome of <see cref="PatternExchange.Decide"/>: either accepted with the new pinnacle key, or a refusal line.</summary>
    public readonly record struct PatternExchangeDecision(bool Accepted, string NewKey, string Refusal);

    /// <summary>
    /// The pure core of the Pattern Exchanger NPC (Docs/Knots/KNOTS-DESIGN.md, "Pattern Exchanger"). A player gives
    /// the NPC a bare Pattern; if they hold a SECOND bare Pattern for the same pinnacle, both are taken and ONE
    /// Pattern for a randomly chosen different pinnacle comes back.
    ///
    /// Everything here is pure: no PropertyManager (its reads THROW under the unit-test harness), no Player, no
    /// WorldObject. <see cref="PatternExchangeStation"/> gathers the inputs and does the inventory work.
    /// </summary>
    public static class PatternExchange
    {
        public const string NotAPatternLine = "I trade in Patterns and nothing else.";
        public const string WovenLine = "This one already has Fibers woven into it. I only take Patterns that have not been started.";
        public const string NoPartnerLine = "Bring me two Patterns for the same pinnacle, and I will give you one for another.";
        public const string NoOtherPinnacleLine = "There is no other pinnacle I can trade you toward right now.";
        public const string NoRoomLine = "You have no room for what I would give you. Make room and come back.";

        public static string SuccessLine(string oldName, string newName)
            => $"Two Patterns of the {oldName}. Here is one of the {newName}.";

        /// <summary>A Pattern is bare when no Fibers have been woven into it (KnotFiberCount absent or 0).</summary>
        public static bool IsBare(int fiberCount) => fiberCount <= 0;

        /// <summary>
        /// Picks the guid of the Pattern to take as the partner: a held item that is a bare Pattern for the SAME
        /// non-empty key, is not the given item itself and is not in an open trade window. The first such item in <paramref name="held"/> order wins.
        /// Returns 0 when there is none.
        /// </summary>
        public static uint FindPartner(uint givenGuid, string givenKey, IEnumerable<PatternCandidate> held)
        {
            if (string.IsNullOrEmpty(givenKey) || held == null)
                return 0;

            foreach (var c in held)
            {
                if (c.Guid == 0 || c.Guid == givenGuid || c.Traded)
                    continue;

                if (!KnotItems.IsPattern(c.Kind) || !IsBare(c.FiberCount))
                    continue;

                if (!string.Equals(c.Key, givenKey, StringComparison.Ordinal))
                    continue;

                return c.Guid;
            }

            return 0;
        }

        /// <summary>
        /// Decides one exchange. <paramref name="availableKeys"/> is the pinnacles a Pattern may name today; the given
        /// key is excluded, and need not be in the list (a Pattern for an unknown or disabled pinnacle can still be
        /// traded away). <paramref name="pick"/> receives the number of candidates (at least 1) and returns an index
        /// in [0, count); an out-of-range answer is clamped, never trusted.
        /// </summary>
        public static PatternExchangeDecision Decide(int givenKind, int givenFiberCount, string givenKey, bool hasPartner,
            IEnumerable<string> availableKeys, Func<int, int> pick)
        {
            if (!KnotItems.IsPattern(givenKind))
                return Refuse(NotAPatternLine);

            if (!IsBare(givenFiberCount))
                return Refuse(WovenLine);

            if (!hasPartner || string.IsNullOrEmpty(givenKey))
                return Refuse(NoPartnerLine);

            var candidates = Candidates(availableKeys, givenKey);

            if (candidates.Count == 0)
                return Refuse(NoOtherPinnacleLine);

            var index = candidates.Count == 1 ? 0 : Math.Clamp(pick(candidates.Count), 0, candidates.Count - 1);

            return new PatternExchangeDecision(true, candidates[index], null);
        }

        /// <summary>
        /// The pinnacle keys a trade may land on: non-empty, not the given key, distinct, ordinal. The ONE filter shared by
        /// <see cref="Decide"/> and the confirmation prompt, so what the prompt names can never drift from what Decide picks from.
        /// </summary>
        public static List<string> Candidates(IEnumerable<string> availableKeys, string givenKey)
            => (availableKeys ?? Enumerable.Empty<string>())
                .Where(k => !string.IsNullOrEmpty(k) && !string.Equals(k, givenKey, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// True when the pinnacle the prompt promised (<paramref name="namedKey"/>, null when the prompt named none) is still the
        /// one the trade would give (<paramref name="newKey"/>). Checked on confirm, so a changed pinnacle list cannot swap the reward.
        /// </summary>
        public static bool PromptStillValid(string namedKey, string newKey)
            => namedKey == null || string.Equals(namedKey, newKey, StringComparison.Ordinal);

        /// <summary>
        /// The Yes/No question asked before a trade. Names the Pattern being turned in (its own display name, falling back to
        /// the standard Pattern name for <paramref name="oldPinnacleName"/>) and, when exactly one pinnacle is possible, the one
        /// coming back. With several candidates the pick is random and none is named.
        /// </summary>
        public static string ConfirmPrompt(string npcName, string givenPatternName, string oldPinnacleName, IReadOnlyList<string> candidatePinnacleNames)
        {
            var given = string.IsNullOrEmpty(givenPatternName) ? KnotItems.PatternName(oldPinnacleName) : givenPatternName;
            var head = $"Trade your {given} and one more Pattern of the {oldPinnacleName} to {npcName} for one Pattern of ";

            if (candidatePinnacleNames != null && candidatePinnacleNames.Count == 1)
                return head + $"the {candidatePinnacleNames[0]}?";

            return head + "another pinnacle, chosen at random?";
        }

        private static PatternExchangeDecision Refuse(string line) => new PatternExchangeDecision(false, null, line);
    }
}
