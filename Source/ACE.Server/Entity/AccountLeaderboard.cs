using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Collapses a leaderboard candidate list down to one entry per account.
    ///
    /// Some boards rank a resource an account shares across all of its characters rather than something
    /// one character earned (banked pyreals is the first of these). Listing every character on such a
    /// board would let one account fill the top 20 with the same pile of money seen from six angles, so
    /// the account has to be reduced to a single row before ranking.
    /// </summary>
    public static class AccountLeaderboard
    {
        /// <summary>
        /// The account a character's pooled balances are keyed on: its account id, falling back to its
        /// own guid when it has no account row.
        ///
        /// The fallback cannot collide with a real account id. Player guids start at
        /// ObjectGuid.PlayerMin (0x50000001) and account ids are small and dense from 1, so the two key
        /// spaces do not overlap, and an orphaned character left by a deleted account simply stands
        /// alone as its own group rather than being merged with every other orphan.
        ///
        /// It also cannot match a row in account_bank, which is keyed by account id - so an orphan reads
        /// a pooled balance of 0, which is the honest answer: an account-less character has no account
        /// pool to draw on.
        ///
        /// Lives here rather than beside either caller because /top bank and the analytics bank snapshot
        /// must key identically or the board and the telemetry silently disagree about who is whom.
        /// </summary>
        public static uint AccountKeyFor(IPlayer player) => player.Account?.AccountId ?? player.Guid.Full;

        /// <summary>
        /// Returns one representative per distinct <paramref name="accountKey"/>: the highest
        /// <paramref name="level"/>, ties broken by the highest <paramref name="tieBreak"/> and then by the
        /// LOWEST <paramref name="ordinal"/> so the choice is deterministic rather than list-order dependent.
        /// Return order is unspecified - the caller sorts.
        ///
        /// The representative is chosen by LEVEL, and it is chosen BEFORE the caller applies its score
        /// filter. Both halves of that are a deliberate product ruling, not a convenience:
        ///
        /// - An account is represented by its main, meaning its highest-level character. Picking whichever
        ///   character happens to hold the score instead would make the board a list of an account's best
        ///   number rather than a list of players, and the name shown would jump between alts as balances
        ///   move.
        /// - The collapse runs BEFORE the caller's score filter, so the representative is chosen without
        ///   reference to the score and an account whose representative scores zero is then dropped
        ///   whole. For a genuinely shared resource, where every character on the account scores the
        ///   same number, that ordering has no observable effect - it matters only for a per-character
        ///   score, where it is what stops the listed name shuffling between alts. Do NOT "fix" either
        ///   case by summing the account: on a shared resource a sum multiplies one balance by the
        ///   account's character count.
        ///
        /// Generic on purpose: the live callers pass IPlayer, and the unit tests drive it with a plain
        /// stand-in type, because the test harness cannot construct a Player.
        /// </summary>
        public static List<T> CollapseToAccountRepresentatives<T>(
            IEnumerable<T> candidates,
            Func<T, uint> accountKey,
            Func<T, int> level,
            Func<T, long> tieBreak,
            Func<T, uint> ordinal)
        {
            var representatives = new List<T>();

            if (candidates == null)
                return representatives;

            foreach (var account in candidates.GroupBy(accountKey))
            {
                representatives.Add(account
                    .OrderByDescending(level)
                    .ThenByDescending(tieBreak)
                    .ThenBy(ordinal)
                    .First());
            }

            return representatives;
        }
    }
}
