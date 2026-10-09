using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    [TestClass]
    public class BattlegroundRespawnPolicyTests
    {
        [TestMethod]
        public void OnDeath_ReturnsConfiguredDelay_AndCoordinatorChoosesSpawn()
        {
            var policy = new BattlegroundRespawnPolicy(17);
            var m = new FakeBattlegroundContext();

            var d = policy.OnDeath(m, m.Member(0, 0));

            var r = d as DeathDisposition.RespawnDisposition;
            Assert.IsNotNull(r);
            Assert.AreEqual(TimeSpan.FromSeconds(17), r.Delay);
            Assert.AreEqual(-1, r.SpawnPointIndex);
        }

        [TestMethod]
        public void OnDeath_IsStateless_SameAnswerForEveryMatchAndSeat()
        {
            var policy = new BattlegroundRespawnPolicy(5);
            var a = new FakeBattlegroundContext();
            var b = new FakeBattlegroundContext();

            Assert.AreEqual(policy.OnDeath(a, a.Member(0, 0)), policy.OnDeath(b, b.Member(1, 1)));
        }
    }
}