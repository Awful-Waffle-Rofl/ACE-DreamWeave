using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// ArenaSpawnPosition.Build: the Position a participant is teleported to must name the map's landblock in its
    /// cell, carry the match's ephemeral instance id unchanged, and put the rotation W last (Position.cs:246).
    /// </summary>
    [TestClass]
    public class ArenaSpawnPositionTests
    {
        private static readonly uint Realm1Instance = Position.InstanceIDFromVars(1, 0x1234, isTemporaryRuleset: true);

        [TestMethod]
        public void Build_CombinesLandblockAndCellAndCarriesTheInstance()
        {
            var a = ArenaMapCatalog.OneVOneSet[0];

            var pos = ArenaSpawnPosition.Build(ArenaMapCatalog.Arena0066, a, Realm1Instance);

            Assert.AreEqual(0x00660113u, pos.Cell);
            Assert.AreEqual((uint)0x0066, (uint)pos.LandblockId.Landblock);
            Assert.AreEqual(Realm1Instance, pos.Instance);
            Assert.IsTrue(pos.IsEphemeralRealm);
            Assert.AreEqual((ushort)1, pos.RealmID);
            Assert.AreEqual(30.00f, pos.PositionX);
            Assert.AreEqual(-12.50f, pos.PositionY);
            Assert.AreEqual(0.005f, pos.PositionZ);
        }

        [TestMethod]
        public void Build_PutsTheYawQuaternionIntoWAndZ()
        {
            var a1 = ArenaMapCatalog.TwoVTwoSet[0];

            var pos = ArenaSpawnPosition.Build(ArenaMapCatalog.Arena0066, a1, Realm1Instance);

            Assert.AreEqual(0.289784f, pos.RotationW);
            Assert.AreEqual(-0.957092f, pos.RotationZ);
            Assert.AreEqual(0f, pos.RotationX);
            Assert.AreEqual(0f, pos.RotationY);
        }

        [TestMethod]
        public void Build_SamePointOnTheTwinMap_OnlyTheLandblockChanges()
        {
            foreach (var point in ArenaMapCatalog.FfaSet)
            {
                var on66 = ArenaSpawnPosition.Build(ArenaMapCatalog.Arena0066, point, Realm1Instance);
                var on67 = ArenaSpawnPosition.Build(ArenaMapCatalog.Arena0067, point, Realm1Instance);

                Assert.AreEqual(0x00660000u | point.CellLow, on66.Cell, point.Label);
                Assert.AreEqual(0x00670000u | point.CellLow, on67.Cell, point.Label);
                Assert.AreEqual(on66.PositionX, on67.PositionX, point.Label);
                Assert.AreEqual(on66.PositionY, on67.PositionY, point.Label);
                Assert.AreEqual(on66.RotationW, on67.RotationW, point.Label);
            }
        }

        [TestMethod]
        public void Build_EveryCatalogPoint_KeepsItsCoordinatesExactly()
        {
            // The constructor only re-derives the cell from coordinates for an outdoor cell of 0; this pins that
            // no authored EnvCell point is moved or re-celled on the way through.
            foreach (var set in new[] { ArenaMapCatalog.OneVOneSet, ArenaMapCatalog.TwoVTwoSet, ArenaMapCatalog.FfaSet })
            {
                foreach (var point in set)
                {
                    var pos = ArenaSpawnPosition.Build(ArenaMapCatalog.Arena0067, point, Realm1Instance);

                    Assert.AreEqual(0x00670000u | point.CellLow, pos.Cell, point.Label);
                    Assert.AreEqual(point.X, pos.PositionX, point.Label);
                    Assert.AreEqual(point.Y, pos.PositionY, point.Label);
                    Assert.AreEqual(point.Z, pos.PositionZ, point.Label);
                    Assert.AreEqual(Realm1Instance, pos.Instance, point.Label);
                }
            }
        }

        [TestMethod]
        public void Build_NonEphemeralInstance_Throws()
        {
            var realm1Default = Position.InstanceIDFromVars(1, 0, isTemporaryRuleset: false);

            Assert.ThrowsExactly<ArgumentException>(() => ArenaSpawnPosition.Build(ArenaMapCatalog.Arena0066, ArenaMapCatalog.OneVOneSet[0], realm1Default));
            Assert.ThrowsExactly<ArgumentException>(() => ArenaSpawnPosition.Build(ArenaMapCatalog.Arena0066, ArenaMapCatalog.OneVOneSet[0], 0));
        }

        [TestMethod]
        public void Build_InstanceInAnotherRealm_Throws()
        {
            var realm0Ephemeral = Position.InstanceIDFromVars(0, 0x1234, isTemporaryRuleset: true);

            Assert.ThrowsExactly<ArgumentException>(() => ArenaSpawnPosition.Build(ArenaMapCatalog.Arena0066, ArenaMapCatalog.OneVOneSet[0], realm0Ephemeral));
        }

        [TestMethod]
        public void Build_MalformedMapOrPoint_Throws()
        {
            var wideLandblock = new ArenaMap("bad", 0x00660000, 1, new Dictionary<string, IReadOnlyList<PvpSpawnPoint>>());
            var outdoorCell = new PvpSpawnPoint("X", 0x0001, 1, 1, 0.005f, 1, 0);

            Assert.ThrowsExactly<ArgumentException>(() => ArenaSpawnPosition.Build(wideLandblock, ArenaMapCatalog.OneVOneSet[0], Realm1Instance));
            Assert.ThrowsExactly<ArgumentException>(() => ArenaSpawnPosition.Build(ArenaMapCatalog.Arena0066, outdoorCell, Realm1Instance));
            Assert.ThrowsExactly<ArgumentNullException>(() => ArenaSpawnPosition.Build(null, ArenaMapCatalog.OneVOneSet[0], Realm1Instance));
            Assert.ThrowsExactly<ArgumentNullException>(() => ArenaSpawnPosition.Build(ArenaMapCatalog.Arena0066, null, Realm1Instance));
        }
    }
}
