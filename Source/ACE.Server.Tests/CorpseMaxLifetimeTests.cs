using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Bounded-lifetime math for player corpses. Both halves of the fix are exercised through the pure
    /// statics on <see cref="Corpse"/> so no Player has to be constructed (Player's static initializer does
    /// not run under test in this repo):
    ///
    /// - <see cref="Corpse.CalculatePlayerCorpseDecayTime"/> is the nominal TimeToRot countdown, floored at
    ///   one hour and capped at the configured maximum lifetime.
    /// - <see cref="Corpse.HasExceededMaxLifetime"/> is the absolute wall-clock deadline enforced when the
    ///   corpse's landblock loads, which is what actually bounds lifetime given that TimeToRot stops being
    ///   decremented entirely while a landblock is unloaded.
    /// </summary>
    [TestClass]
    public class CorpseMaxLifetimeTests
    {
        /// <summary>The shipped default for player_corpse_max_lifetime_seconds: 7 days.</summary>
        private const long OneWeek = 604800;

        // ---- half B: the clamp on the level-scaled decay timer ----

        [TestMethod]
        public void DecayTime_BelowTheFloor_IsRaisedToOneHour()
        {
            // 1 * 300 and 11 * 300 are both under the 3600 second floor
            Assert.AreEqual(3600.0, Corpse.CalculatePlayerCorpseDecayTime(1, OneWeek));
            Assert.AreEqual(3600.0, Corpse.CalculatePlayerCorpseDecayTime(11, OneWeek));
        }

        [TestMethod]
        public void DecayTime_BetweenFloorAndCap_IsFiveMinutesPerLevel()
        {
            Assert.AreEqual(3600.0, Corpse.CalculatePlayerCorpseDecayTime(12, OneWeek));   // exactly the floor
            Assert.AreEqual(3900.0, Corpse.CalculatePlayerCorpseDecayTime(13, OneWeek));
            Assert.AreEqual(82500.0, Corpse.CalculatePlayerCorpseDecayTime(275, OneWeek)); // retail personal cap
        }

        [TestMethod]
        public void DecayTime_AboveTheCap_IsClampedToTheCap()
        {
            // 604800 / 300 = 2016, so level 2016 lands exactly on the cap and anything past it exceeds it.
            // Levels this high are reachable: the personal maximum is 275 + 5 per enlightenment.
            Assert.AreEqual(604800.0, Corpse.CalculatePlayerCorpseDecayTime(2016, OneWeek));
            Assert.AreEqual(604800.0, Corpse.CalculatePlayerCorpseDecayTime(2017, OneWeek));
            Assert.AreEqual(604800.0, Corpse.CalculatePlayerCorpseDecayTime(100000, OneWeek));
        }

        [TestMethod]
        public void DecayTime_CapOfZero_DisablesTheClamp()
        {
            Assert.AreEqual(30000000.0, Corpse.CalculatePlayerCorpseDecayTime(100000, 0));

            // the floor still applies with the cap disabled
            Assert.AreEqual(3600.0, Corpse.CalculatePlayerCorpseDecayTime(1, 0));
        }

        [TestMethod]
        public void DecayTime_CapBelowTheFloor_WinsOverTheFloor()
        {
            // the cap is the harder guarantee: an operator who asks for a 60 second maximum gets 60 seconds
            Assert.AreEqual(60.0, Corpse.CalculatePlayerCorpseDecayTime(1, 60));
            Assert.AreEqual(60.0, Corpse.CalculatePlayerCorpseDecayTime(275, 60));
        }

        // ---- half A: the absolute wall-clock deadline ----

        [TestMethod]
        public void Deadline_YoungCorpse_HasNotExpired()
        {
            var created = 1_000_000;

            Assert.IsFalse(Corpse.HasExceededMaxLifetime(created, created, OneWeek));
            Assert.IsFalse(Corpse.HasExceededMaxLifetime(created, created + 1, OneWeek));
            Assert.IsFalse(Corpse.HasExceededMaxLifetime(created, created + OneWeek - 1, OneWeek));
        }

        [TestMethod]
        public void Deadline_AtOrPastTheDeadline_HasExpired()
        {
            var created = 1_000_000;

            Assert.IsTrue(Corpse.HasExceededMaxLifetime(created, created + OneWeek, OneWeek));
            Assert.IsTrue(Corpse.HasExceededMaxLifetime(created, created + OneWeek + 1, OneWeek));

            // the case this fix is actually for: a landblock nobody visited for a month
            Assert.IsTrue(Corpse.HasExceededMaxLifetime(created, created + (30 * 86400), OneWeek));
        }

        [TestMethod]
        public void Deadline_CapOfZero_NeverExpires()
        {
            var created = 1_000_000;

            Assert.IsFalse(Corpse.HasExceededMaxLifetime(created, created + (365 * 86400), 0));
            Assert.IsFalse(Corpse.HasExceededMaxLifetime(created, created + (365 * 86400), -1));
        }

        [TestMethod]
        public void Deadline_NoCreationTimestamp_NeverExpires()
        {
            // a corpse we cannot age is left to the ordinary TimeToRot countdown rather than destroyed
            Assert.IsFalse(Corpse.HasExceededMaxLifetime(null, 1_000_000 + (365 * 86400), OneWeek));
        }

        [TestMethod]
        public void Deadline_ClockSkewBackwards_DoesNotExpire()
        {
            // a corpse whose creation timestamp is in the future must not be treated as ancient
            var created = 2_000_000;

            Assert.IsFalse(Corpse.HasExceededMaxLifetime(created, created - OneWeek, OneWeek));
        }
    }
}
