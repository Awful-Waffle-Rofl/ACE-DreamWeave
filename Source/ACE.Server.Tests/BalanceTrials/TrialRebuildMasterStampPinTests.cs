using System;
using System.Linq;

using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// Source pin: the live MasterStamp lookup must read the shard DB via GetBiota(guid, true) (doNotAddToCache, so the caching override disposes its context). Masters are parked characters (IsDeleted, AccountId 0),
    /// which PlayerManager.Initialize never loads, so a PlayerManager read always reports "no stamp". The live delegate needs
    /// a database and cannot run here, so the assignment line itself is pinned. Reads no PropertyManager keys.
    /// </summary>
    [TestClass]
    public class TrialRebuildMasterStampPinTests
    {
        [TestMethod]
        public void Live_MasterStamp_reads_the_shard_db_not_PlayerManager()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/BalanceTrials/TrialRebuild.cs");
            var start = source.IndexOf("internal static Lookups Live()", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "Live() not found");
            var live = source.Substring(start);

            var lines = live.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("MasterStamp =")).ToList();
            Assert.AreEqual(1, lines.Count, "exactly one MasterStamp assignment in Live()");

            StringAssert.Contains(lines[0], "DatabaseManager.Shard.BaseDatabase.GetBiota(guid, true)");
            StringAssert.Contains(lines[0], "(ushort)PropertyString.TrialArchetypeStamp");
            Assert.IsFalse(lines[0].Contains("PlayerManager"), "masters are parked and never in PlayerManager");
        }
    }
}
