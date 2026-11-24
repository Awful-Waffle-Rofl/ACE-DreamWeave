using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Auth;
using ACE.Entity;
using ACE.Server.Avatars;
using ACE.Server.Entity;

namespace ACE.Server.Tests.Avatars
{
    [TestClass]
    public class AvatarRostersTests
    {
        /// <summary>Minimal IPlayer: only Guid, Account and AllegianceOfficerRank answer; the rest throws.</summary>
        public class FakePlayer : DispatchProxy
        {
            public ObjectGuid Guid;
            public Account Account;
            public int? OfficerRank;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case "get_Guid": return Guid;
                    case "get_Account": return Account;
                    case "get_AllegianceOfficerRank": return OfficerRank;
                    default: throw new NotSupportedException(targetMethod.Name);
                }
            }

            public static IPlayer Make(uint guid, uint accountId, int? officerRank = null)
            {
                var p = Create<IPlayer, FakePlayer>();
                var f = (FakePlayer)(object)p;
                f.Guid = new ObjectGuid(guid);
                f.Account = new Account { AccountId = accountId };
                f.OfficerRank = officerRank;
                return p;
            }
        }

        [TestMethod]
        public void Depth_CountsStepsToMonarch()
        {
            var monarch = new AllegianceNode(new ObjectGuid(0x50000001), null);
            var vassal = new AllegianceNode(new ObjectGuid(0x50000002), null, monarch, monarch);
            var grandVassal = new AllegianceNode(new ObjectGuid(0x50000003), null, monarch, vassal);

            Assert.AreEqual(0, AvatarRosters.DepthOf(monarch));
            Assert.AreEqual(1, AvatarRosters.DepthOf(vassal));
            Assert.AreEqual(2, AvatarRosters.DepthOf(grandVassal));
        }

        private static (AllegianceNode m, AllegianceNode v, AllegianceNode g, Dictionary<uint, IPlayer> players) Tree()
        {
            var m = new AllegianceNode(new ObjectGuid(0x50000001), null);
            var v = new AllegianceNode(new ObjectGuid(0x50000002), null, m, m);
            var g = new AllegianceNode(new ObjectGuid(0x50000003), null, m, v);
            var players = new Dictionary<uint, IPlayer>
            {
                [0x50000001] = FakePlayer.Make(0x50000001, 11),
                [0x50000002] = FakePlayer.Make(0x50000002, 12, officerRank: 2),
                [0x50000003] = FakePlayer.Make(0x50000003, 13),
            };
            return (m, v, g, players);
        }

        [TestMethod]
        public void BuildRoster_MapsRankOfficerDepthAndAccount()
        {
            var (m, v, g, players) = Tree();
            v.Rank = 4;
            var roster = AvatarRosters.BuildRoster(m, new[] { m, v, g }, n => players[n.PlayerGuid.Full], p => false);

            Assert.AreEqual(0x50000001u, roster.MonarchGuid);
            Assert.AreEqual(11u, roster.MonarchAccountId);
            Assert.IsFalse(roster.MonarchDeleted);
            Assert.AreEqual(3, roster.Members.Count);
            Assert.AreEqual(new KeeperCandidate(0x50000002, 12, 4, 2, 1), roster.Members[1]);
            Assert.AreEqual(2, roster.Members[2].Depth);
        }

        [TestMethod]
        public void BuildRoster_SkipsDeletedMembers_ThroughTheSeam()
        {
            var (m, v, g, players) = Tree();
            var deleted = players[0x50000002];
            var roster = AvatarRosters.BuildRoster(m, new[] { m, v, g }, n => players[n.PlayerGuid.Full], p => ReferenceEquals(p, deleted));

            Assert.AreEqual(2, roster.Members.Count);
            Assert.IsFalse(roster.Members.Any(x => x.Guid == 0x50000002));
            Assert.IsFalse(roster.MonarchDeleted);
        }

        [TestMethod]
        public void BuildRoster_DeletedMonarch_SetsFlagAndLeavesRoster()
        {
            var (m, v, g, players) = Tree();
            var deleted = players[0x50000001];
            var roster = AvatarRosters.BuildRoster(m, new[] { m, v, g }, n => players[n.PlayerGuid.Full], p => ReferenceEquals(p, deleted));

            Assert.IsTrue(roster.MonarchDeleted);
            Assert.AreEqual(11u, roster.MonarchAccountId);
            Assert.IsFalse(roster.Members.Any(x => x.Guid == 0x50000001));
        }

        [TestMethod]
        public void InMemoryIsDeleted_OfflineCharacterCountsAsNotDeleted_WithoutTouchingTheDatabase()
        {
            // The fake throws on any member other than Guid/Account/officer rank, so a DB-backed IsDeleted read would fail.
            Assert.IsFalse(AvatarRosters.InMemoryIsDeleted(FakePlayer.Make(1, 1)));
        }
    }
}
