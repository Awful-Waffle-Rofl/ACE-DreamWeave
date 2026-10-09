using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// ObjectiveLock is a pure, in-memory counter meant to back a puzzle gate inside a per-run dungeon
    /// instance - a door that opens when every creature in a room is dead, a gate that needs three of five
    /// bells rung, a gate that needs three levers held at once. It carries no state that would survive a
    /// character across runs (unlike quest stamps), so it needs no world, session or database to exercise.
    /// </summary>
    [TestClass]
    public class ObjectiveLockTests
    {
        private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }

        [TestMethod]
        public void Contribute_SixDistinctTokensReachingThreshold_ReturnsTrueOnlyOnTheSixth()
        {
            var t0 = Utc(2026, 9, 1);
            var gate = new ObjectiveLock(6);

            Assert.IsFalse(gate.Contribute("bell-1", 1, t0));
            Assert.IsFalse(gate.Contribute("bell-2", 1, t0));
            Assert.IsFalse(gate.Contribute("bell-3", 1, t0));
            Assert.IsFalse(gate.Contribute("bell-4", 1, t0));
            Assert.IsFalse(gate.Contribute("bell-5", 1, t0));
            Assert.IsTrue(gate.Contribute("bell-6", 1, t0), "the sixth distinct token is what crosses the threshold");
        }

        [TestMethod]
        public void Contribute_SameTokenRepeatedly_DoesNotAccumulate()
        {
            var t0 = Utc(2026, 9, 1);
            var gate = new ObjectiveLock(6);

            for (var i = 0; i < 6; i++)
                Assert.IsFalse(gate.Contribute("lever-1", 1, t0), "one lever pulled repeatedly must never satisfy a multi-contributor gate on its own");

            Assert.AreEqual(1, gate.CurrentWeight(t0), "repeated contributions from the same key replace, they do not add");
        }

        [TestMethod]
        public void Contribute_AnExpiredTokenIsDropped_SoAThreeTokenGateDoesNotFire()
        {
            var t0 = Utc(2026, 9, 1, 12, 0, 0);
            var shortExpiry = t0.AddSeconds(5);
            var longExpiry = t0.AddSeconds(100);
            var gate = new ObjectiveLock(3);

            Assert.IsFalse(gate.Contribute("lever-1", 1, t0, shortExpiry));
            Assert.IsFalse(gate.Contribute("lever-2", 1, t0.AddSeconds(1), longExpiry));

            // lever-1's token has now expired by the time the third arrives; lever-2 is still live because it
            // was given a longer expiry, and lever-3 is given its own fresh expiry so it isn't dropped the
            // instant it lands
            var afterExpiry = t0.AddSeconds(10);
            Assert.IsFalse(gate.Contribute("lever-3", 1, afterExpiry, afterExpiry.AddSeconds(30)), "with lever-1 expired, only two of the three levers are live");
            Assert.AreEqual(2, gate.CurrentWeight(afterExpiry), "lever-2 and lever-3 remain live; lever-1 was purged");
        }

        [TestMethod]
        public void Contribute_AllThreeTokensArriveInsideTheWindow_FiresExactlyOnce()
        {
            var t0 = Utc(2026, 9, 1, 12, 0, 0);
            var expiry = t0.AddSeconds(30);
            var gate = new ObjectiveLock(3);

            Assert.IsFalse(gate.Contribute("lever-1", 1, t0, expiry));
            Assert.IsFalse(gate.Contribute("lever-2", 1, t0.AddSeconds(5), expiry));
            Assert.IsTrue(gate.Contribute("lever-3", 1, t0.AddSeconds(10), expiry), "all three levers held inside the window must fire the gate");

            // a further contribution after satisfaction must never fire again
            Assert.IsFalse(gate.Contribute("lever-4", 1, t0.AddSeconds(11), expiry));
        }

        [TestMethod]
        public void Contribute_AfterAlreadySatisfied_ReturnsFalse()
        {
            var t0 = Utc(2026, 9, 1);
            var gate = new ObjectiveLock(1);

            Assert.IsTrue(gate.Contribute("kill-1", 1, t0));
            Assert.IsFalse(gate.Contribute("kill-2", 1, t0.AddSeconds(1)), "a contribution after satisfaction must return false even though the lock remains satisfied");
        }

        [TestMethod]
        public void Reset_ClearsAnUnlatchedLock_SoItCanBeSatisfiedFreshAfterwards()
        {
            var t0 = Utc(2026, 9, 1);
            var gate = new ObjectiveLock(2);

            Assert.IsFalse(gate.Contribute("lever-1", 1, t0));
            gate.Reset();

            Assert.AreEqual(0, gate.CurrentWeight(t0), "reset clears contributed tokens while unlatched");

            Assert.IsFalse(gate.Contribute("lever-1", 1, t0));
            Assert.IsTrue(gate.Contribute("lever-2", 1, t0), "the lock must still be satisfiable after an unlatched reset");
        }

        [TestMethod]
        public void Reset_AfterLatching_IsANoOp()
        {
            var t0 = Utc(2026, 9, 1);
            var gate = new ObjectiveLock(1);

            Assert.IsTrue(gate.Contribute("kill-1", 1, t0));

            gate.Reset();

            Assert.IsTrue(gate.Latched, "a reset after the gate has opened must never re-close it");
            Assert.AreEqual(1, gate.CurrentWeight(t0), "the tokens behind an already-open gate are not cleared by reset");
        }

        [TestMethod]
        public void Contribute_SingleTokenMeetingTheThresholdOnItsOwn_Satisfies()
        {
            var t0 = Utc(2026, 9, 1);
            var gate = new ObjectiveLock(2);

            Assert.IsTrue(gate.Contribute("switch-1", 2, t0), "one token whose weight alone meets the threshold must satisfy the lock");
        }

        [TestMethod]
        public void Contribute_ATokenThatIsAlreadyExpiredOnArrival_NeverCounts()
        {
            // The purge runs BEFORE the insert, so a token whose expiry is already at or behind `now` is not
            // caught by it. If Contribute summed the raw dictionary it would count this token exactly once -
            // and once is enough to latch a gate open permanently, with no way back. A mis-authored expiry of
            // zero, or clock skew, is all it would take.
            var t0 = Utc(2026, 9, 1);
            var lock1 = new ObjectiveLock(1);

            Assert.IsFalse(lock1.Contribute("stale", 1, t0, t0), "an expiry equal to now is already expired");
            Assert.IsFalse(lock1.Latched, "and it must not have latched the gate");
            Assert.AreEqual(0, lock1.CurrentWeight(t0), "Contribute and CurrentWeight must agree about it");

            Assert.IsFalse(lock1.Contribute("stale-2", 1, t0, t0.AddSeconds(-30)), "an expiry in the past likewise");
            Assert.IsFalse(lock1.Latched);

            // and a live token still works on the same lock, so the guard is not simply refusing everything
            Assert.IsTrue(lock1.Contribute("live", 1, t0, t0.AddSeconds(20)));
        }

        [TestMethod]
        
        public void Contribute_ZeroOrNegativeThreshold_IsSatisfiedByTheFirstContribution()
        {
            // Design choice, documented on ObjectiveLock.Contribute: a threshold of zero or less describes a
            // gate with nothing left to prove, so it latches open on the very first contribution rather than
            // being permanently (and un-announceably) already-open before anything happens.
            var t0 = Utc(2026, 9, 1);

            var zero = new ObjectiveLock(0);
            Assert.IsTrue(zero.Contribute("any", 0, t0), "a zero threshold is satisfied by the first contribution");
            Assert.IsFalse(zero.Contribute("any-2", 0, t0.AddSeconds(1)), "latched after the first call, like any other lock");

            var negative = new ObjectiveLock(-5);
            Assert.IsTrue(negative.Contribute("any", 0, t0), "a negative threshold is also trivially satisfied by the first contribution");
        }
    }
}
