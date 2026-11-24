using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars;
using ACE.Server.WorldEvents;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>The pure seams behind the feed's context gathering: World Event state, the split earner and the fellowship tally.</summary>
    [TestClass]
    public class AvatarFeedContextTests
    {
        private const uint Earner = 1001;
        private const uint Fellow = 1002;

        private static WorldEvent EventWith(uint? avatarId, params uint[] participants)
        {
            var evt = new WorldEvent(1, null, new WorldEventRequest { AvatarId = avatarId }, null);

            foreach (var guid in participants)
                evt.Participation.CreditCombatContact(guid, 0, null, "p");

            return evt;
        }

        [TestMethod]
        public void AvatarFeedState_PrivateEvent_ParticipantIsPrivate()
        {
            Assert.AreEqual((true, true), WorldEventHooks.AvatarFeedState(EventWith(7, Earner), Earner));
        }

        [TestMethod]
        public void AvatarFeedState_PublicEvent_ParticipantIsNotPrivate()
        {
            Assert.AreEqual((true, false), WorldEventHooks.AvatarFeedState(EventWith(null, Earner), Earner));
        }

        [TestMethod]
        public void AvatarFeedState_NoRecord_OrNoEvent_IsNeither()
        {
            Assert.AreEqual((false, false), WorldEventHooks.AvatarFeedState(EventWith(7, Earner), Fellow));
            Assert.AreEqual((false, false), WorldEventHooks.AvatarFeedState(null, Earner));
        }

        [TestMethod]
        public void PrivateEvent_FellowsShareOfAnEarnersKill_DoesNotFeed()
        {
            var evt = EventWith(7, Earner); // only the earner has a record
            System.Func<uint, (bool, bool)> state = g => WorldEventHooks.AvatarFeedState(evt, g);

            Assert.IsTrue(AvatarFeed.PrivateEventApplies(state(Earner), Earner, 0, state), "the earner's own grant");
            Assert.IsFalse(AvatarFeed.PrivateEventApplies(state(Fellow), Fellow, 0, state), "no split in flight: the fellow alone is not in the event");
            Assert.IsTrue(AvatarFeed.PrivateEventApplies(state(Fellow), Fellow, Earner, state), "the fellow's share inside the earner's split");
        }

        [TestMethod]
        public void PublicEvent_FellowsShare_IsNotSuppressed()
        {
            var evt = EventWith(null, Earner);
            System.Func<uint, (bool, bool)> state = g => WorldEventHooks.AvatarFeedState(evt, g);

            Assert.IsFalse(AvatarFeed.PrivateEventApplies(state(Fellow), Fellow, Earner, state));
        }

        [TestMethod]
        public void InGauntletCopy_NoContribution()
        {
            var asked = new List<uint>();
            System.Func<uint, bool> earnerIn = g => { asked.Add(g); return false; };

            Assert.IsTrue(AvatarFeed.GauntletCopyApplies(true, Fellow, 0, earnerIn), "the receiver stands in a copy");
            Assert.IsTrue(AvatarFeed.GauntletCopyApplies(true, Earner, Earner, earnerIn), "the earner's own grant in a copy");
            Assert.AreEqual(0, asked.Count, "a receiver in a copy settles it without asking about the earner");
            Assert.IsFalse(AvatarFeed.GauntletCopyApplies(false, Fellow, 0, earnerIn), "positive control: outside a copy, no split in flight, the feed runs");
        }

        [TestMethod]
        public void SplitFromGauntletEarner_NoContribution()
        {
            System.Func<uint, bool> earnerIn = g => g == Earner;

            Assert.IsTrue(AvatarFeed.GauntletCopyApplies(false, Fellow, Earner, earnerIn), "the fellow's share inside a copy-standing earner's split");
            Assert.IsFalse(AvatarFeed.GauntletCopyApplies(false, Fellow, 1003, earnerIn), "an earner outside a copy does not suppress the share");
            Assert.IsFalse(AvatarFeed.GauntletCopyApplies(false, Earner, Earner, earnerIn), "the earner is not asked about itself: its own state is the receiver state");
        }

        private static int Tally(IEnumerable<(uint? accountId, uint? monarch, bool inRange)> members, uint monarch)
        {
            var tally = new AvatarFeed.SameAllegianceTally();

            foreach (var m in members)
                tally.Add(m.accountId, m.monarch, m.inRange, monarch);

            return tally.Count;
        }

        private static int Count(uint monarch, params (uint? acct, uint? m, bool inRange)[] members) => Tally(members, monarch);

        [TestMethod]
        public void Tally_TwoAccountsSameAllegiance_IsTwo() => Assert.AreEqual(2, Count(9, (1u, 9u, true), (2u, 9u, true)));

        [TestMethod]
        public void Tally_OneAccountTwoCharacters_IsOne() => Assert.AreEqual(1, Count(9, (1u, 9u, true), (1u, 9u, true)));

        [TestMethod]
        public void Tally_DifferentMonarch_IsNotCounted() => Assert.AreEqual(1, Count(9, (1u, 9u, true), (2u, 8u, true)));

        [TestMethod]
        public void Tally_OutOfRange_IsNotCounted() => Assert.AreEqual(1, Count(9, (1u, 9u, true), (2u, 9u, false)));

        [TestMethod]
        public void Tally_NoMonarchOrNoAccount_IsNotCounted() => Assert.AreEqual(1, Count(9, (1u, 9u, true), (2u, null, true), (null, 9u, true)));

        [TestMethod]
        public void Tally_SaturatesAtTwo() => Assert.AreEqual(2, Count(9, (1u, 9u, true), (2u, 9u, true), (3u, 9u, true)));

        [TestMethod]
        public void ReceivedAfterCap_Clamps()
        {
            Assert.AreEqual(1000L, AvatarFeed.ReceivedAfterCap(1000, 10, 0, 100, 5000));
            Assert.AreEqual(300L, AvatarFeed.ReceivedAfterCap(1000, 99, 4700, 100, 5000));
            Assert.AreEqual(0L, AvatarFeed.ReceivedAfterCap(1000, 100, 5000, 100, 5000));
            Assert.AreEqual(0L, AvatarFeed.ReceivedAfterCap(1000, 99, 5000, 100, 5000));
        }
    }
}
