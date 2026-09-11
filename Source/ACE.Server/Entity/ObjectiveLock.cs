using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Entity
{
    /// <summary>
    /// An in-memory, count-based puzzle gate: "the lock opens once enough distinct contributions have been
    /// made" - every creature in a room dead, three of five bells rung, three levers held down at once. This
    /// is deliberately NOT built on quest stamps. A stamp is keyed to the CHARACTER, so on a per-instance
    /// dungeon (the intended attachment point - see the class-level task note this was built against) a
    /// player's second run would start the counter already at its cap from the first run, and the gate would
    /// stand permanently open with nothing left to solve. An <see cref="ObjectiveLock"/> instead lives on the
    /// world object (or the instance) that owns the puzzle, so it resets for free the moment that instance is
    /// rebuilt - there is no persisted state to carry over between runs.
    ///
    /// This class knows nothing about <c>WorldObject</c>, <c>Player</c>, <c>Landblock</c> or the database. It
    /// is a pure counter so the puzzle rules (does this combination of contributions satisfy the gate) can be
    /// unit-tested with no server and no live world; something else is responsible for creating one, feeding
    /// it contributions from game events, and acting on its return value.
    ///
    /// Time is taken as an explicit <see cref="DateTime"/> parameter on every method that needs "now", rather
    /// than read from a clock internally (no <c>DateTime.UtcNow</c>, no <c>Time.GetUnixTime()</c> anywhere in
    /// this file). A lock that read its own clock could not be driven through an expiry window in a unit test
    /// without a real sleep; passing time in lets a test assert "these three tokens arrive one second apart,
    /// with a two-second expiry" instantly and deterministically. Callers should pass the same time
    /// representation the rest of the server's live code already uses for "now" at the call site.
    /// </summary>
    public class ObjectiveLock
    {
        private class Token
        {
            public double Weight;
            public DateTime? ExpiresAt;
        }

        private readonly Dictionary<string, Token> _tokens = new Dictionary<string, Token>();

        /// <summary>
        /// The summed weight the live (non-expired) tokens must reach or exceed for the lock to be satisfied.
        /// </summary>
        public double Required { get; }

        /// <summary>
        /// TRUE once <see cref="Contribute"/> has returned TRUE exactly one time in this lock's lifetime. This
        /// is the LATCH: it exists because a puzzle gate is meant to open once, permanently, not to flicker
        /// open and shut as tokens expire and get replaced around the threshold. Without a latch, a door that
        /// derived "open" purely from the live sum could close again the instant one contributing token
        /// expired - which would strand a player who had already walked through it. The latch is the
        /// authoritative "has this ever fired" record; nothing after the first TRUE can undo it, including a
        /// later drop in the live sum.
        /// </summary>
        public bool Latched { get; private set; }

        public ObjectiveLock(double required)
        {
            Required = required;
        }

        /// <summary>
        /// Contributes (or replaces) one token toward the lock, then reports whether this call was the
        /// transition into the satisfied state.
        ///
        /// Contributing the same <paramref name="tokenKey"/> a second time REPLACES its earlier weight and
        /// expiry rather than adding to it. This is what stops a single repeatable action - one lever pulled
        /// over and over, one bell struck twice - from satisfying a gate meant to require several distinct
        /// contributors: "lever 1" can only ever be worth one lever's weight no matter how many times it is
        /// pulled, because each pull overwrites the same dictionary entry instead of accumulating a new one.
        ///
        /// Expiry is purged lazily, at the start of this call, rather than on a timer or a background thread.
        /// This is safe precisely because the summed weight is only ever read here, inside a contribution: an
        /// expired token can never be counted, because by the time anything asks "is the lock satisfied" this
        /// method has already dropped it. A timer would add a moving part (a thread, a scheduled callback)
        /// to buy nothing a lazy purge does not already guarantee.
        ///
        /// <see cref="Contribute"/> returns TRUE only the FIRST time the summed weight reaches
        /// <see cref="Required"/>; every call after that - including one made while the lock is still
        /// satisfied - returns FALSE. See <see cref="Latched"/> for why.
        /// </summary>
        /// <param name="tokenKey">Identity of the contributor. Contributing the same key again replaces
        /// its previous entry.</param>
        /// <param name="weight">How much this token is worth toward <see cref="Required"/>.</param>
        /// <param name="now">The current time, supplied by the caller.</param>
        /// <param name="expiresAt">The time this token stops counting, or null for a token that never
        /// expires. A value at or before <paramref name="now"/> is accepted but never contributes: it is
        /// excluded from this call's own total and purged on the next one.</param>
        /// <returns>TRUE if, and only if, this call is the one that first satisfies the lock.</returns>
        public bool Contribute(string tokenKey, double weight, DateTime now, DateTime? expiresAt = null)
        {
            if (string.IsNullOrEmpty(tokenKey))
                throw new ArgumentException("tokenKey must not be null or empty.", nameof(tokenKey));

            PurgeExpired(now);

            _tokens[tokenKey] = new Token { Weight = weight, ExpiresAt = expiresAt };

            if (Latched)
                return false;

            // A non-positive Required is treated as trivially already satisfied - a threshold of zero or less
            // describes a gate with nothing left to prove, so the very first contribution (which is also the
            // first opportunity this lock ever gets to report TRUE) latches it open. This keeps "already met"
            // and "just became met" the same event rather than inventing a separate always-true state that
            // Contribute could never actually announce.
            // Deliberately CurrentWeight rather than a raw sum over _tokens: the token just inserted above
            // has not been through PurgeExpired, which ran before the insert. A token whose expiry is
            // already at or behind `now` - a mis-authored expiry of zero, or clock skew - would otherwise
            // count on this one call and could latch the gate open permanently, which is unrecoverable.
            // This also keeps Contribute and CurrentWeight answering the same question; when they disagreed,
            // a progress display could read "2 of 3" on a gate that had already opened.
            var sum = CurrentWeight(now);

            if (sum >= Required)
            {
                Latched = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Clears every contributed token - the "wrong answer" punishment path, e.g. a lever released or a
        /// wrong bell struck resets the room's progress. This is a no-op once <see cref="Latched"/> is TRUE:
        /// a gate that has already opened must never be re-closeable by a later reset, or a player who solved
        /// the puzzle and stepped away could come back to find their progress erased retroactively, or a
        /// door could physically re-lock behind someone standing in the doorway.
        /// </summary>
        public void Reset()
        {
            if (Latched)
                return;

            _tokens.Clear();
        }

        /// <summary>
        /// The current summed weight of live (non-expired) tokens as of <paramref name="now"/>, without
        /// mutating anything. Useful for progress display ("3 of 5 bells rung") without driving the lock.
        /// </summary>
        public double CurrentWeight(DateTime now)
        {
            return _tokens.Values
                .Where(t => !IsExpired(t, now))
                .Sum(t => t.Weight);
        }

        private void PurgeExpired(DateTime now)
        {
            if (_tokens.Count == 0)
                return;

            var expiredKeys = _tokens.Where(kv => IsExpired(kv.Value, now)).Select(kv => kv.Key).ToList();

            foreach (var key in expiredKeys)
                _tokens.Remove(key);
        }

        private static bool IsExpired(Token token, DateTime now)
        {
            return token.ExpiresAt.HasValue && token.ExpiresAt.Value <= now;
        }
    }
}
