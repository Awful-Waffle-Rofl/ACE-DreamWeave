using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// Paid-match counts per opposing ACCOUNT pair per arena day (Docs/Pvp/DESIGN.md "Repeat-pairing cap"). Pure and
    /// in-memory: a restart resets it (accepted). Keys are ordered account pairs, low id first, so (a, b) and (b, a) are
    /// one entry. Account ids are the key, never character ids, so playing the same opponent on a different character
    /// does not reset the count.
    /// </summary>
    public sealed class PvpPairLedger
    {
        private readonly Dictionary<(uint Low, uint High), (int Day, int Count)> counts = new Dictionary<(uint, uint), (int, int)>();

        /// <summary>The ordered key for two accounts: lower id first.</summary>
        public static (uint Low, uint High) KeyOf(uint accountA, uint accountB) =>
            accountA <= accountB ? (accountA, accountB) : (accountB, accountA);

        /// <summary>
        /// The account a character's counts are keyed on: its account id, or its own character id when none is known
        /// (<paramref name="accountId"/> 0). The live adapter fills the account id with AccountLeaderboard.AccountKeyFor, so
        /// this is the same rule with the same fallback.
        /// </summary>
        public static uint AccountKey(uint accountId, uint characterId) => accountId != 0 ? accountId : characterId;

        /// <summary>
        /// Paid matches already counted for the pair on <paramref name="today"/>. A stored day EARLIER than today reads as
        /// 0; a stored day at or after today counts as today (the clock stepping back must not reopen the cap -
        /// <see cref="PvpArenaRewards.DecideCap"/>'s rule).
        /// </summary>
        public int CountFor(uint accountA, uint accountB, int today)
        {
            if (!counts.TryGetValue(KeyOf(accountA, accountB), out var entry))
                return 0;

            return entry.Day >= today ? Math.Max(0, entry.Count) : 0;
        }

        /// <summary>True when the pair has reached <paramref name="cap"/> on <paramref name="today"/>. A cap of 0 or less is off.</summary>
        public bool IsAtCap(uint accountA, uint accountB, int today, long cap) =>
            cap > 0 && CountFor(accountA, accountB, today) >= cap;

        /// <summary>
        /// Counts one paid-eligible match for the pair on <paramref name="day"/>, saturating at <paramref name="cap"/> (no
        /// cap = saturating at int.MaxValue). Drops every entry from an earlier day first, so the table cannot grow
        /// without bound.
        /// </summary>
        public void Record(uint accountA, uint accountB, int day, long cap)
        {
            foreach (var stale in counts.Where(kv => kv.Value.Day < day).Select(kv => kv.Key).ToList())
                counts.Remove(stale);

            var key = KeyOf(accountA, accountB);
            var current = counts.TryGetValue(key, out var entry) && entry.Day >= day ? Math.Max(0, entry.Count) : 0;
            var effectiveDay = counts.TryGetValue(key, out var e2) ? Math.Max(e2.Day, day) : day;
            var ceiling = cap > 0 ? (int)Math.Min(cap, int.MaxValue) : int.MaxValue;

            counts[key] = (effectiveDay, Math.Min(ceiling, current == int.MaxValue ? current : current + 1));
        }

        /// <summary>
        /// Whether a seat is an unpaid rematch: any cross-team account pair it belongs to is at the cap. Decided per
        /// player so a rotating 2v2 partner cannot reset the count - each opposing account is a pair of its own.
        /// </summary>
        /// <param name="seatAccount">The seat's account key.</param>
        /// <param name="opponentAccounts">The account keys of every player on the other team(s).</param>
        public bool IsUnpaidRematch(uint seatAccount, IEnumerable<uint> opponentAccounts, int today, long cap)
        {
            if (cap <= 0)
                return false;

            foreach (var opponent in opponentAccounts)
                if (opponent != seatAccount && IsAtCap(seatAccount, opponent, today, cap))
                    return true;

            return false;
        }
    }
}
