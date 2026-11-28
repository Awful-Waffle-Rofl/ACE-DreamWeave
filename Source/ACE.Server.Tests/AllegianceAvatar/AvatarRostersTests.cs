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

        // ---- stale allegiance filter (FromAllegiances) -------------------------------------------------------

        /// <summary>One fake allegiance: identity object, monarch node, members, and the players behind them.</summary>
        private static (object Key, AllegianceNode Monarch, IEnumerable<AllegianceNode> Members) FakeAllegiance(object key, uint monarchGuid, uint vassalGuid, Dictionary<uint, IPlayer> players)
        {
            var m = new AllegianceNode(new ObjectGuid(monarchGuid), null);
            var v = new AllegianceNode(new ObjectGuid(vassalGuid), null, m, m);
            players[monarchGuid] = FakePlayer.Make(monarchGuid, monarchGuid);
            players[vassalGuid] = FakePlayer.Make(vassalGuid, vassalGuid);
            return (key, m, new[] { m, v });
        }

        [TestMethod]
        public void FromAllegiances_KeepsAnAllegianceThatIsItsMonarchsCurrentOne()
        {
            var players = new Dictionary<uint, IPlayer>();
            var key = new object();
            var a = FakeAllegiance(key, 0x50000001, 0x50000002, players);

            var rosters = AvatarRosters.FromAllegiances(new[] { a }, guid => guid == 0x50000001 ? key : null, p => false, n => players[n.PlayerGuid.Full]);

            Assert.AreEqual(1, rosters.Count);
            Assert.AreEqual(0x50000001u, rosters[0].MonarchGuid);
        }

        [TestMethod]
        public void FromAllegiances_DropsAStaleAllegiance_WhoseMonarchNowPointsAtAnotherOne()
        {
            var players = new Dictionary<uint, IPlayer>();
            var stale = new object();
            var current = new object();
            var staleEntry = FakeAllegiance(stale, 0x50000001, 0x50000002, players);   // former monarch, left behind in Allegiances
            var realEntry = FakeAllegiance(current, 0x50000009, 0x5000000A, players);   // the allegiance they swore into

            // The stale monarch's node now belongs to the real allegiance; the real monarch's node is its own.
            object CurrentOf(uint guid) => guid == 0x50000001 || guid == 0x50000009 ? current : null;
            var rosters = AvatarRosters.FromAllegiances(new[] { staleEntry, realEntry }, CurrentOf, p => false, n => players[n.PlayerGuid.Full]);

            Assert.AreEqual(1, rosters.Count, "only the allegiance its monarch still leads is a roster");
            Assert.AreEqual(0x50000009u, rosters[0].MonarchGuid);
        }

        [TestMethod]
        public void FromAllegiances_DropsAnAllegiance_WhoseMonarchHasNoNode()
        {
            var players = new Dictionary<uint, IPlayer>();
            var a = FakeAllegiance(new object(), 0x50000001, 0x50000002, players);

            var rosters = AvatarRosters.FromAllegiances(new[] { a }, guid => null, p => false, n => players[n.PlayerGuid.Full]);

            Assert.AreEqual(0, rosters.Count);
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
