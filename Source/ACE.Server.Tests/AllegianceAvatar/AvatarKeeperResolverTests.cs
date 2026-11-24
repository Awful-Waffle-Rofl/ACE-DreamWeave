using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    [TestClass]
    public class AvatarKeeperResolverTests
    {
        private const double Now = 1_800_000_000.0;
        private const double Day = 86400.0;
        private static readonly AvatarTuning T = AvatarTuning.Defaults; // idle 30d, active window 7d

        private const uint Monarch = 1;
        private const uint MonarchAcct = 100;

        private static AccountActivityIndex Seen(params (uint Account, double DaysAgo)[] rows)
        {
            var index = new AccountActivityIndex();
            foreach (var (account, daysAgo) in rows)
                index.Touch(account, Now - daysAgo * Day);
            return index;
        }

        private static KeeperCandidate C(uint guid, uint rank, int officer, int depth) =>
            new KeeperCandidate(guid, 100 + guid, rank, officer, depth);

        private static KeeperResult Resolve(uint? heir, List<KeeperCandidate> members, AccountActivityIndex a, bool monarchDeleted = false) =>
            AvatarKeeperResolver.Resolve(Monarch, MonarchAcct, monarchDeleted, heir, members, a, Now, T);

        [TestMethod]
        public void MonarchActive_IsKeeper()
        {
            var r = Resolve(2, new List<KeeperCandidate> { C(2, 5, 3, 1) }, Seen((MonarchAcct, 1), (102, 0)));
            Assert.AreEqual(Monarch, r.KeeperGuid);
            Assert.IsFalse(r.IsActing);
            Assert.IsFalse(r.MonarchIdle);
        }

        [TestMethod]
        public void MonarchAccountSeenOnAlt_IsNotIdle()
        {
            // The monarch character itself was last seen 40 days ago, an alt on the same account 1 day ago.
            var activity = AccountActivityIndex.Build(new (uint, double)[]
            {
                (MonarchAcct, Now - 40 * Day), (MonarchAcct, Now - 1 * Day), (102, Now),
            });
            var r = Resolve(2, new List<KeeperCandidate> { C(2, 5, 3, 1) }, activity);
            Assert.IsFalse(r.IsActing);
            Assert.AreEqual(Monarch, r.KeeperGuid);
        }

        [TestMethod]
        public void Idle_HeirActive_IsKeeper()
        {
            var members = new List<KeeperCandidate> { C(2, 9, 3, 1), C(3, 1, 0, 2) };
            var r = Resolve(3, members, Seen((MonarchAcct, 40), (102, 0), (103, 2)));
            Assert.AreEqual(3u, r.KeeperGuid);
            Assert.IsTrue(r.IsActing);
            Assert.IsTrue(r.MonarchIdle);
        }

        [TestMethod]
        public void Idle_HeirInactive_FallsToHighestOfficer()
        {
            var members = new List<KeeperCandidate> { C(2, 9, 0, 1), C(3, 1, 1, 2), C(4, 1, 2, 3), C(5, 1, 3, 4) };
            // Heir (5, officer 3) inactive; 4 (officer 2) beats 3 (officer 1) despite lower rank than 2.
            var r = Resolve(5, members, Seen((MonarchAcct, 40), (102, 0), (103, 0), (104, 1), (105, 20)));
            Assert.AreEqual(4u, r.KeeperGuid);
            Assert.IsTrue(r.IsActing);
        }

        [TestMethod]
        public void Idle_OfficerTie_CloserToMonarchWins()
        {
            var members = new List<KeeperCandidate> { C(2, 3, 2, 3), C(3, 3, 2, 1), C(4, 3, 2, 2) };
            var r = Resolve(null, members, Seen((MonarchAcct, 40), (102, 0), (103, 0), (104, 0)));
            Assert.AreEqual(3u, r.KeeperGuid);
        }

        [TestMethod]
        public void Idle_NoOfficers_HighestRankWins()
        {
            var members = new List<KeeperCandidate> { C(2, 2, 0, 1), C(3, 6, 0, 3), C(4, 6, 0, 2) };
            var r = Resolve(null, members, Seen((MonarchAcct, 40), (102, 0), (103, 0), (104, 0)));
            Assert.AreEqual(4u, r.KeeperGuid); // rank tie 3 vs 4, closer to monarch wins
            Assert.IsTrue(r.IsActing);
        }

        [TestMethod]
        public void Idle_FullTie_LowestGuidWins()
        {
            var members = new List<KeeperCandidate> { C(7, 4, 0, 2), C(5, 4, 0, 2), C(6, 4, 0, 2) };
            var r = Resolve(null, members, Seen((MonarchAcct, 40), (105, 0), (106, 0), (107, 0)));
            Assert.AreEqual(5u, r.KeeperGuid);
        }

        [TestMethod]
        public void Idle_NoActiveMember_MonarchStaysKeeper()
        {
            var members = new List<KeeperCandidate> { C(2, 4, 1, 1), C(3, 2, 0, 2) };
            var r = Resolve(2, members, Seen((MonarchAcct, 40), (102, 30), (103, 9)));
            Assert.AreEqual(Monarch, r.KeeperGuid);
            Assert.IsFalse(r.IsActing);
            Assert.IsTrue(r.MonarchIdle);
        }

        [TestMethod]
        public void Idle_MonarchsOwnAccountAlt_IsNotConsideredAsActing()
        {
            // Unreachable in practice (an alt login makes the monarch not idle), but the exclusion is pinned
            // by listing the monarch's account as a candidate while the index holds it as idle.
            var members = new List<KeeperCandidate> { new KeeperCandidate(2, MonarchAcct, 9, 3, 1) };
            var r = Resolve(null, members, Seen((MonarchAcct, 40)));
            Assert.AreEqual(Monarch, r.KeeperGuid);
            Assert.IsFalse(r.IsActing);
        }

        [TestMethod]
        public void DeletedMonarch_ActiveAccount_IsIdle_ActingKeeperChosen()
        {
            // The monarch's account was seen today, but the monarch character is deleted.
            var members = new List<KeeperCandidate> { C(2, 5, 1, 1) };
            var r = Resolve(null, members, Seen((MonarchAcct, 0), (102, 0)), monarchDeleted: true);
            Assert.AreEqual(2u, r.KeeperGuid);
            Assert.IsTrue(r.IsActing);
            Assert.IsTrue(r.MonarchIdle);
        }

        [TestMethod]
        public void Idle_OfficerFullTie_LowestGuidWins()
        {
            var members = new List<KeeperCandidate> { C(9, 3, 2, 2), C(4, 3, 2, 2), C(6, 3, 2, 2) };
            var r = Resolve(null, members, Seen((MonarchAcct, 40), (104, 0), (106, 0), (109, 0)));
            Assert.AreEqual(4u, r.KeeperGuid);
        }

        [TestMethod]
        public void Idle_HeirNotInMembers_FallsThrough()
        {
            // Heir guid 50 is not on the roster even though its account is active; the officer wins.
            var members = new List<KeeperCandidate> { C(2, 5, 1, 1), C(3, 8, 0, 1) };
            var activity = Seen((MonarchAcct, 40), (102, 0), (103, 0), (150, 0));
            var r = Resolve(50, members, activity);
            Assert.AreEqual(2u, r.KeeperGuid);
            Assert.IsTrue(r.IsActing);
        }

        [TestMethod]
        public void MonarchSeenExactlyIdleDaysAgo_IsStillActive()
        {
            var r = Resolve(2, new List<KeeperCandidate> { C(2, 5, 1, 1) }, Seen((MonarchAcct, 30), (102, 0)));
            Assert.IsFalse(r.IsActing);
            Assert.IsFalse(r.MonarchIdle);

            var idle = Resolve(2, new List<KeeperCandidate> { C(2, 5, 1, 1) }, Seen((MonarchAcct, 30.001), (102, 0)));
            Assert.IsTrue(idle.IsActing);
        }

        [TestMethod]
        public void MemberSeenExactlyActiveWindowDaysAgo_IsStillActive()
        {
            var members = new List<KeeperCandidate> { C(2, 5, 1, 1) };
            var r = Resolve(null, members, Seen((MonarchAcct, 40), (102, 7)));
            Assert.AreEqual(2u, r.KeeperGuid);

            var out_ = Resolve(null, members, Seen((MonarchAcct, 40), (102, 7.001)));
            Assert.AreEqual(Monarch, out_.KeeperGuid);
            Assert.IsFalse(out_.IsActing);
        }
    }
}
