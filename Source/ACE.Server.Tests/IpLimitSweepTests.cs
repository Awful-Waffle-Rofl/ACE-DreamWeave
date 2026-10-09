using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE: IP active-player limit - the pure part of the periodic sweep
    /// (IpLimitManager.SelectAccountRepresentatives).
    ///
    /// The sweep itself (IpLimitManager.Tick) is not covered here: it reads PropertyManager, PlayerManager
    /// and live Session/Player state, and would need a running world to exercise. Only its one piece of
    /// non-trivial decision logic - collapsing several in-world characters that share an ACCOUNT down to the
    /// one that is really online - is factored out as a pure function, and that is what these pin.
    /// </summary>
    [TestClass]
    public class IpLimitSweepTests
    {
        private static KeyValuePair<uint, double> A(uint accountId, double loginTimestamp)
        {
            return new KeyValuePair<uint, double>(accountId, loginTimestamp);
        }

        [TestMethod]
        public void Empty_ReturnsEmpty()
        {
            var kept = IpLimitManager.SelectAccountRepresentatives(new List<KeyValuePair<uint, double>>());

            Assert.AreEqual(0, kept.Count);
        }

        [TestMethod]
        public void Null_ReturnsEmpty()
        {
            var kept = IpLimitManager.SelectAccountRepresentatives(null);

            Assert.AreEqual(0, kept.Count);
        }

        [TestMethod]
        public void DistinctAccounts_AllKept()
        {
            var entries = new List<KeyValuePair<uint, double>>
            {
                A(10, 100),
                A(11, 200),
                A(12, 300),
            };

            var kept = IpLimitManager.SelectAccountRepresentatives(entries);

            CollectionAssert.AreEqual(new List<int> { 0, 1, 2 }, kept);
        }

        /// <summary>
        /// The reconnect case this helper exists for: one account appears twice because the previous session
        /// has not been reaped yet. The NEWER entry (higher LoginTimestamp) is the live one and must be the
        /// survivor; counting both would push the address over its cap and make the player evict themselves.
        /// </summary>
        [TestMethod]
        public void DuplicateAccount_KeepsHighestLoginTimestamp()
        {
            var entries = new List<KeyValuePair<uint, double>>
            {
                A(10, 100), // stale prior session
                A(10, 500), // the live one
            };

            var kept = IpLimitManager.SelectAccountRepresentatives(entries);

            CollectionAssert.AreEqual(new List<int> { 1 }, kept);
        }

        [TestMethod]
        public void DuplicateAccount_NewerFirst_StillKeepsHighestLoginTimestamp()
        {
            var entries = new List<KeyValuePair<uint, double>>
            {
                A(10, 500), // the live one, listed first this time
                A(10, 100),
            };

            var kept = IpLimitManager.SelectAccountRepresentatives(entries);

            CollectionAssert.AreEqual(new List<int> { 0 }, kept);
        }

        [TestMethod]
        public void DuplicateAccount_TiedTimestamps_KeepsLowestIndexDeterministically()
        {
            var entries = new List<KeyValuePair<uint, double>>
            {
                A(10, 250),
                A(10, 250),
                A(10, 250),
            };

            var kept = IpLimitManager.SelectAccountRepresentatives(entries);

            CollectionAssert.AreEqual(new List<int> { 0 }, kept);
        }

        [TestMethod]
        public void MixedAccounts_OnePerAccount_IndicesAscending()
        {
            var entries = new List<KeyValuePair<uint, double>>
            {
                A(10, 100),
                A(11, 900),
                A(10, 700), // newer entry for account 10
                A(12, 400),
                A(11, 200), // older entry for account 11
            };

            var kept = IpLimitManager.SelectAccountRepresentatives(entries);

            CollectionAssert.AreEqual(new List<int> { 1, 2, 3 }, kept);
        }

        /// <summary>
        /// End to end over the two pure pieces the sweep composes: dedupe by account first, then apply the
        /// rule. Without the dedupe, the stale duplicate would take the group to three "outside" characters
        /// against maxFree 1 and would select two violators, one of which is the reconnecting player's own
        /// live character.
        /// </summary>
        [TestMethod]
        public void DedupeThenSelectViolators_StaleSessionDoesNotEvictItsOwnAccount()
        {
            // guid, level, confined, loginTimestamp - parallel to accountLogins below
            var players = new List<IpLimitCandidate>
            {
                new IpLimitCandidate(1, 50, false, 100), // account 10, stale session
                new IpLimitCandidate(1, 50, false, 500), // account 10, live session, same character
                new IpLimitCandidate(2, 80, false, 300), // account 11
            };

            var accountLogins = new List<KeyValuePair<uint, double>>
            {
                A(10, 100),
                A(10, 500),
                A(11, 300),
            };

            var kept = IpLimitManager.SelectAccountRepresentatives(accountLogins);
            var candidates = kept.Select(i => players[i]).ToList();

            Assert.AreEqual(2, candidates.Count);

            var violators = IpLimitManager.SelectViolators(candidates, maxFree: 1, maxConfined: 2);

            // Two characters outside against maxFree 1: exactly one violator, the lower level one.
            Assert.AreEqual(1, violators.Count);
            Assert.AreEqual(1u, violators[0]);
        }

        // ==================================================================================
        // ClampCap - narrowing the ip_limit_max_* config longs to the ints the rule takes.
        // ==================================================================================

        [TestMethod]
        public void ClampCap_OrdinaryValues_PassThroughUnchanged()
        {
            Assert.AreEqual(0, IpLimitManager.ClampCap(0));
            Assert.AreEqual(1, IpLimitManager.ClampCap(1));
            Assert.AreEqual(2, IpLimitManager.ClampCap(2));
            Assert.AreEqual(3, IpLimitManager.ClampCap(3));
            Assert.AreEqual(1000, IpLimitManager.ClampCap(1000));
        }

        /// <summary>
        /// Zero is a legitimate configuration for both caps and must survive the clamp: maxFree 0 means no
        /// character from an address may be outside a mule landblock at all. The floor is 0, not 1, so this
        /// pins that the clamp does not quietly promote a deliberate 0 into a 1.
        /// </summary>
        [TestMethod]
        public void ClampCap_Zero_IsPreservedNotRaisedToOne()
        {
            Assert.AreEqual(0, IpLimitManager.ClampCap(0));
        }

        [TestMethod]
        public void ClampCap_Negative_ClampsToZero()
        {
            Assert.AreEqual(0, IpLimitManager.ClampCap(-1));
            Assert.AreEqual(0, IpLimitManager.ClampCap(-5000000000L));
            Assert.AreEqual(0, IpLimitManager.ClampCap(long.MinValue));
        }

        /// <summary>
        /// The fat-fingered admin case: an extra digit, or a pasted millisecond timestamp. A bare (int) cast
        /// is unchecked in C# and wraps - (int)5000000000L is 705032704, and (int)4294967296L is 0 - so the
        /// intent "effectively no limit" would land somewhere arbitrary, and a wrap into the negatives would
        /// make the outside cap negative and select every character on every non-exempt address. Saturating at
        /// int.MaxValue keeps the meaning the admin intended.
        /// </summary>
        [TestMethod]
        public void ClampCap_HugeValues_SaturateAtIntMaxValue()
        {
            Assert.AreEqual(int.MaxValue, IpLimitManager.ClampCap(5000000000L));
            Assert.AreEqual(int.MaxValue, IpLimitManager.ClampCap(4294967296L));
            Assert.AreEqual(int.MaxValue, IpLimitManager.ClampCap((long)int.MaxValue + 1));
            Assert.AreEqual(int.MaxValue, IpLimitManager.ClampCap(long.MaxValue));
        }

        [TestMethod]
        public void ClampCap_IntMaxValue_PassesThroughExactly()
        {
            Assert.AreEqual(int.MaxValue, IpLimitManager.ClampCap(int.MaxValue));
        }

        /// <summary>
        /// The lockout the clamp exists to prevent, shown end to end.
        ///
        /// 4294967295 (0xFFFFFFFF - a plausible fat-finger, and exactly what a pasted 32-bit sentinel looks
        /// like) narrows under a bare cast to NEGATIVE 1. In SelectViolatorIndices the outside cap then reads
        /// "outside.Count (3) > maxFree (-1)", and the excess is "3 - (-1)" = 4, so Take(4) sweeps up ALL
        /// THREE characters: every character on every non-exempt address becomes a violator. Through ClampCap
        /// the same config saturates to int.MaxValue and selects nobody, which is what the admin meant.
        ///
        /// Worth knowing, because it is counter-intuitive and it invalidates the obvious test: a value that
        /// wraps to an EXTREME negative is accidentally harmless. At maxFree -2147483647 the excess
        /// computation "3 - (-2147483647)" overflows int itself, wraps negative, and Take(negative) returns
        /// empty - so nobody is selected. The dangerous wraps are the ones landing on SMALL negatives, which
        /// is why this test pins -1 and not the largest value available.
        /// </summary>
        [TestMethod]
        public void ClampCap_NegativeWrappingConfig_DoesNotSelectEveryone()
        {
            var candidates = new List<IpLimitCandidate>
            {
                new IpLimitCandidate(1, 50, false, 100),
                new IpLimitCandidate(2, 80, false, 200),
                new IpLimitCandidate(3, 20, false, 300),
            };

            const long fatFingered = 4294967295L; // (int)fatFingered == -1

            // Sanity: the unchecked narrowing really does go negative, which is what makes this dangerous.
            Assert.AreEqual(-1, unchecked((int)fatFingered));

            var withClamp = IpLimitManager.SelectViolators(candidates, IpLimitManager.ClampCap(fatFingered), IpLimitManager.ClampCap(2));
            Assert.AreEqual(0, withClamp.Count);

            var withBareCast = IpLimitManager.SelectViolators(candidates, unchecked((int)fatFingered), 2);
            Assert.AreEqual(3, withBareCast.Count, "regression guard: this is the lockout ClampCap prevents");
        }
    }
}
