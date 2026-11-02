using System;
using System.Linq;

using ACE.Server.Knots;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Knots
{
    /// <summary>
    /// Review 2026-10-19 item 4: a refund or loss report an encounter could not deliver to an offline owner is held in
    /// memory and applied when they are next online. The store is static, so every test clears it before and after.
    /// </summary>
    [TestClass]
    public class KnotPendingEventsTests
    {
        [TestInitialize]
        [TestCleanup]
        public void Reset() => KnotPendingEvents.ClearForTest();

        [TestMethod]
        public void Record_KeepsEachOwnersEventsInOrder_AndApartFromOtherOwners()
        {
            KnotPendingEvents.Record(1u, 10u, KnotPendingEvents.Kind.Refund);
            KnotPendingEvents.Record(2u, 20u, KnotPendingEvents.Kind.AttemptLost);
            KnotPendingEvents.Record(1u, 11u, KnotPendingEvents.Kind.AttemptLost);
            KnotPendingEvents.Record(1u, 12u, KnotPendingEvents.Kind.BossKilled);

            CollectionAssert.AreEqual(new[] { (10u, KnotPendingEvents.Kind.Refund), (11u, KnotPendingEvents.Kind.AttemptLost), (12u, KnotPendingEvents.Kind.BossKilled) }, KnotPendingEvents.Peek(1u).ToArray());
            CollectionAssert.AreEqual(new[] { (20u, KnotPendingEvents.Kind.AttemptLost) }, KnotPendingEvents.Peek(2u).ToArray());
            Assert.AreEqual(0, KnotPendingEvents.Peek(3u).Count);
        }

        [TestMethod]
        public void Take_ReturnsAndForgets_SoEachEventIsAppliedOnce()
        {
            KnotPendingEvents.Record(1u, 10u, KnotPendingEvents.Kind.Refund);
            KnotPendingEvents.Record(1u, 10u, KnotPendingEvents.Kind.Refund);

            Assert.AreEqual(2, KnotPendingEvents.Take(1u).Count, "two refunds for the same Knot are two events");
            Assert.AreEqual(0, KnotPendingEvents.Take(1u).Count, "taken once");
            Assert.AreEqual(0, KnotPendingEvents.Peek(1u).Count);
        }

        [TestMethod]
        public void Apply_WithoutALiveOwner_TakesNothing()
        {
            KnotPendingEvents.Record(1u, 10u, KnotPendingEvents.Kind.Refund);

            Assert.AreEqual(0, KnotPendingEvents.Apply(null));
            Assert.AreEqual(1, KnotPendingEvents.Peek(1u).Count, "still waiting for the owner");
        }

        [TestMethod]
        public void TheSeamAndTheLoginApplyPendingEvents()
        {
            var seam = PooledLootSourceText.Read("Source/ACE.Server/Knots/KnotSeam.cs");
            var use = PooledLootSourceText.MethodBody(seam, "private static bool RunUse(");
            var apply = use.IndexOf("KnotPendingEvents.Apply(player)", StringComparison.Ordinal);
            var gate = use.IndexOf("KnotUse.Gate(", StringComparison.Ordinal);
            var read = use.IndexOf("knot.GetProperty(PropertyInt.Structure)", StringComparison.Ordinal);

            Assert.IsTrue(apply >= 0 && apply < gate && apply < read, "applied before the gate and before the entry count is read");

            var login = PooledLootSourceText.Read("Source/ACE.Server/Managers/WorldManager.cs");
            StringAssert.Contains(login, "ACE.Server.Knots.KnotPendingEvents.ScheduleApplyOnLogin(session.Player);");

            var store = PooledLootSourceText.Read("Source/ACE.Server/Knots/KnotPendingEvents.cs");
            StringAssert.Contains(store, "IN MEMORY ONLY");
            StringAssert.Contains(store, "KnotEncounterEvents.RefundEntry(owner, knotGuid)");
            StringAssert.Contains(store, "KnotEncounterEvents.OnAttemptLost(owner, knotGuid)");
            StringAssert.Contains(store, "KnotEncounterEvents.OnBossKilled(owner, knotGuid)");

            // a held loss or kill that consumed the Knot ends the use with the ordinary spent line
            var gone = use.IndexOf("player.FindObject(knot.Guid.Full, Player.SearchLocations.MyInventory) == null", apply, StringComparison.Ordinal);
            var spent = use.IndexOf("Say(player, KnotItems.KnotSpent);", gone, StringComparison.Ordinal);
            Assert.IsTrue(gone > apply && spent > gone && spent < gate, "consumed: say spent and stop before the gate");
        }
    }
}
