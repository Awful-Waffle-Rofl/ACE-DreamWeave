using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    [TestClass]
    public class BattlegroundFixturePlacerTests
    {
        private static BattlegroundSealPiece P(uint wcid) => new BattlegroundSealPiece(wcid, 0x016C0100, 10f, 20f, 0f, 1f, 0f);

        [TestMethod]
        public void AllPiecesPlace_CompletesWithTheCount()
        {
            var job = new BattlegroundFixtureJob();
            var placed = new List<uint>();

            BattlegroundFixturePlacer.Run(new[] { P(1), P(2), P(3) }, () => true, p => { placed.Add(p.Wcid); return null; }, job);

            Assert.AreEqual(BattlegroundFixtureStatus.Placed, job.Status);
            Assert.AreEqual(3, job.Placed);
            CollectionAssert.AreEqual(new uint[] { 1, 2, 3 }, placed);
        }

        [TestMethod]
        public void NoPieces_CompletesWithZero()
        {
            var job = new BattlegroundFixtureJob();

            BattlegroundFixturePlacer.Run(Array.Empty<BattlegroundSealPiece>(), () => true, p => "never", job);

            Assert.AreEqual(BattlegroundFixtureStatus.Placed, job.Status);
            Assert.AreEqual(0, job.Placed);
        }

        [TestMethod]
        public void SecondPieceRefused_FailsAndStops()
        {
            var job = new BattlegroundFixtureJob();
            var attempts = 0;

            BattlegroundFixturePlacer.Run(new[] { P(1), P(2), P(3) }, () => true, p => { attempts++; return p.Wcid == 2 ? "refused" : null; }, job);

            Assert.AreEqual(BattlegroundFixtureStatus.Failed, job.Status);
            Assert.AreEqual(1, job.Placed);
            Assert.AreEqual("refused", job.Detail);
            Assert.AreEqual(2, attempts, "placement stops at the first refusal");
        }

        [TestMethod]
        public void PlaceThrows_FailsInsteadOfEscaping()
        {
            var job = new BattlegroundFixtureJob();

            BattlegroundFixturePlacer.Run(new[] { P(1), P(2) }, () => true, p => p.Wcid == 2 ? throw new InvalidOperationException("boom") : null, job);

            Assert.AreEqual(BattlegroundFixtureStatus.Failed, job.Status);
            Assert.AreEqual(1, job.Placed);
            StringAssert.Contains(job.Detail, "boom");
        }

        [TestMethod]
        public void InstanceNoLongerLive_FailsWithoutPlacing()
        {
            var job = new BattlegroundFixtureJob();
            var attempts = 0;

            BattlegroundFixturePlacer.Run(new[] { P(1) }, () => false, p => { attempts++; return null; }, job);

            Assert.AreEqual(BattlegroundFixtureStatus.Failed, job.Status);
            Assert.AreEqual(0, attempts);
        }

        [TestMethod]
        public void LiveAdapters_ImplementBothBattlegroundSeams()
        {
            // The coordinator enables the bg room only when its gateway and spaces ALSO implement the bg seams (found with
            // `as`), so dropping either interface from the live adapters silently turns battlegrounds off in production.
            Assert.IsTrue(typeof(IBattlegroundPlayerGateway).IsAssignableFrom(typeof(ACE.Server.Pvp.LivePvpPlayerGateway)));
            Assert.IsTrue(typeof(IBattlegroundMatchSpaces).IsAssignableFrom(typeof(ACE.Server.Pvp.LivePvpMatchSpaces)));
        }

        // ---- best effort (the zone markers) ----

        [TestMethod]
        public void BestEffort_ContinuesPastARefusal_CompletesWithTheCountAndTheFirstRefusal()
        {
            var job = new BattlegroundFixtureJob();
            var attempts = new List<uint>();

            BattlegroundFixturePlacer.Run(new[] { P(1), P(2), P(3), P(4) }, () => true,
                p => { attempts.Add(p.Wcid); return p.Wcid == 2 ? "first refusal" : p.Wcid == 3 ? "second refusal" : null; }, job, bestEffort: true);

            Assert.AreEqual(BattlegroundFixtureStatus.Placed, job.Status);
            Assert.AreEqual(2, job.Placed);
            Assert.AreEqual("first refusal", job.Detail);
            CollectionAssert.AreEqual(new uint[] { 1, 2, 3, 4 }, attempts, "every piece is attempted");
        }

        [TestMethod]
        public void BestEffort_ContinuesPastAThrow()
        {
            var job = new BattlegroundFixtureJob();
            var attempts = 0;

            BattlegroundFixturePlacer.Run(new[] { P(1), P(2), P(3) }, () => true,
                p => { attempts++; return p.Wcid == 1 ? throw new InvalidOperationException("boom") : null; }, job, bestEffort: true);

            Assert.AreEqual(BattlegroundFixtureStatus.Placed, job.Status);
            Assert.AreEqual(2, job.Placed);
            Assert.AreEqual(3, attempts);
            StringAssert.Contains(job.Detail, "boom");
        }

        [TestMethod]
        public void BestEffort_AllPlaced_HasNoDetail()
        {
            var job = new BattlegroundFixtureJob();

            BattlegroundFixturePlacer.Run(new[] { P(1), P(2) }, () => true, p => null, job, bestEffort: true);

            Assert.AreEqual(BattlegroundFixtureStatus.Placed, job.Status);
            Assert.AreEqual(2, job.Placed);
            Assert.IsNull(job.Detail);
        }

        [TestMethod]
        public void BestEffort_DeadInstance_StillFails()
        {
            var job = new BattlegroundFixtureJob();
            var attempts = 0;

            BattlegroundFixturePlacer.Run(new[] { P(1), P(2) }, () => false, p => { attempts++; return null; }, job, bestEffort: true);

            Assert.AreEqual(BattlegroundFixtureStatus.Failed, job.Status);
            Assert.AreEqual(0, attempts);
        }
        [TestMethod]
        public void NullPiece_Fails()
        {
            var job = new BattlegroundFixtureJob();

            BattlegroundFixturePlacer.Run(new[] { P(1), null }, () => true, p => null, job);

            Assert.AreEqual(BattlegroundFixtureStatus.Failed, job.Status);
            Assert.AreEqual(1, job.Placed);
        }
    }
}
