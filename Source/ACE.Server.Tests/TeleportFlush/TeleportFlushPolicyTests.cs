using System.Linq;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.TeleportFlush
{
    /// <summary>
    /// TeleportFlushPolicy: the pure decisions behind teleport_flush_scope / _delete_pacing / _delete_rate.
    /// </summary>
    [TestClass]
    public class TeleportFlushPolicyTests
    {
        private static readonly uint Realm0 = Position.InstanceIDFromVars(0, 0, false);
        private static readonly uint Realm1 = Position.InstanceIDFromVars(1, 0, false);

        private const uint TownNetworkCell = 0x00070147;
        private const uint LifestoneCell = 0x00160142;

        private static ulong At(uint cell, uint instance)
        {
            return new Position(cell, 0, 0, 0, 0, 0, 0, 1, instance).InstancedLandblock;
        }

        [TestMethod]
        public void Normalize_OutOfRangeReadsAsLegacy()
        {
            Assert.AreEqual(TeleportFlushScope.Off, TeleportFlushPolicy.Normalize(0));
            Assert.AreEqual(TeleportFlushScope.Legacy, TeleportFlushPolicy.Normalize(1));
            Assert.AreEqual(TeleportFlushScope.Ledger, TeleportFlushPolicy.Normalize(2));

            foreach (var bad in new long[] { -1, 3, 100, long.MinValue, long.MaxValue })
                Assert.AreEqual(TeleportFlushScope.Legacy, TeleportFlushPolicy.Normalize(bad), $"scope {bad}");
        }

        [TestMethod]
        public void NormalizeRate_ZeroOrLessCountsAsOne()
        {
            Assert.AreEqual(1, TeleportFlushPolicy.NormalizeRate(0));
            Assert.AreEqual(1, TeleportFlushPolicy.NormalizeRate(-5));
            Assert.AreEqual(10, TeleportFlushPolicy.NormalizeRate(10));
            Assert.AreEqual(int.MaxValue, TeleportFlushPolicy.NormalizeRate(long.MaxValue));
        }

        [TestMethod]
        public void Defaults_AreTheNoOpShip()
        {
            var defaults = TeleportFlushSettings.Defaults;

            Assert.AreEqual(TeleportFlushScope.Legacy, defaults.Scope);
            Assert.IsFalse(defaults.Pacing);
            Assert.AreEqual(10, defaults.Rate);
        }

        [TestMethod]
        public void Decide_Legacy_UsesTheLegacyGateExactly()
        {
            var cases = new[]
            {
                (At(TownNetworkCell, Realm1), At(LifestoneCell, Realm1)),   // landblock change, same instance
                (At(TownNetworkCell, Realm1), At(TownNetworkCell, Realm0)), // instance change, same landblock id
                (At(TownNetworkCell, Realm0), At(TownNetworkCell, Realm0)), // no change
                (At(LifestoneCell, Realm0), At(TownNetworkCell, Realm1)),   // both change
            };

            foreach (var (origin, destination) in cases)
            {
                var legacyGate = Player.TeleportRequiresClientObjectFlush(origin, destination);

                Assert.AreEqual(legacyGate ? TeleportFlushAction.LegacyFlush : TeleportFlushAction.None,
                    TeleportFlushPolicy.Decide(TeleportFlushScope.Legacy, false, origin, destination));

                Assert.AreEqual(legacyGate ? TeleportFlushAction.DeferredFlush : TeleportFlushAction.None,
                    TeleportFlushPolicy.Decide(TeleportFlushScope.Legacy, true, origin, destination));
            }
        }

        [TestMethod]
        public void Decide_Off_PurgesSilentlyOnInstanceChangeOnly()
        {
            Assert.AreEqual(TeleportFlushAction.SilentPurge,
                TeleportFlushPolicy.Decide(TeleportFlushScope.Off, false, At(TownNetworkCell, Realm1), At(TownNetworkCell, Realm0)));

            Assert.AreEqual(TeleportFlushAction.None,
                TeleportFlushPolicy.Decide(TeleportFlushScope.Off, false, At(TownNetworkCell, Realm1), At(LifestoneCell, Realm1)));

            Assert.AreEqual(TeleportFlushAction.None,
                TeleportFlushPolicy.Decide(TeleportFlushScope.Off, true, At(TownNetworkCell, Realm1), At(LifestoneCell, Realm1)),
                "pacing changes nothing under scope 0");
        }

        [TestMethod]
        public void Decide_Ledger_AlwaysFires_AndPacingIsInert()
        {
            var crossInstance = TeleportFlushAction.SilentPurge | TeleportFlushAction.ObserverDeletes | TeleportFlushAction.LedgerFire;

            foreach (var pacing in new[] { false, true })
            {
                Assert.AreEqual(crossInstance,
                    TeleportFlushPolicy.Decide(TeleportFlushScope.Ledger, pacing, At(TownNetworkCell, Realm1), At(TownNetworkCell, Realm0)));

                Assert.AreEqual(TeleportFlushAction.LedgerFire,
                    TeleportFlushPolicy.Decide(TeleportFlushScope.Ledger, pacing, At(TownNetworkCell, Realm1), At(LifestoneCell, Realm1)));

                Assert.AreEqual(TeleportFlushAction.LedgerFire,
                    TeleportFlushPolicy.Decide(TeleportFlushScope.Ledger, pacing, At(TownNetworkCell, Realm0), At(TownNetworkCell, Realm0)));
            }
        }

        [TestMethod]
        public void InstanceAndLandblockOf_SplitInstancedLandblock()
        {
            var value = At(TownNetworkCell, Realm1);

            Assert.AreEqual(Realm1, TeleportFlushPolicy.InstanceOf(value));
            Assert.AreEqual((ushort)0x0007, TeleportFlushPolicy.LandblockOf(value));
        }

        [TestMethod]
        public void Neighbourhood_IsThe3x3_ClampedTo0To0xFE_CentreFirst()
        {
            var middle = TeleportFlushPolicy.Neighbourhood(0x7F7F);
            Assert.AreEqual(9, middle.Count);
            Assert.AreEqual((ushort)0x7F7F, middle[0]);
            CollectionAssert.AreEquivalent(new ushort[] { 0x7E7E, 0x7E7F, 0x7E80, 0x7F7E, 0x7F7F, 0x7F80, 0x807E, 0x807F, 0x8080 }, middle.ToArray());

            // x == 0: no x-1 column
            var townNetwork = TeleportFlushPolicy.Neighbourhood(0x0007);
            CollectionAssert.AreEquivalent(new ushort[] { 0x0006, 0x0007, 0x0008, 0x0106, 0x0107, 0x0108 }, townNetwork.ToArray());

            // 0xFE edge: no 0xFF row or column
            var edge = TeleportFlushPolicy.Neighbourhood(0xFEFE);
            CollectionAssert.AreEquivalent(new ushort[] { 0xFDFD, 0xFDFE, 0xFEFD, 0xFEFE }, edge.ToArray());

            // a 0xFF coordinate is outside the range but the landblock itself is always included
            var outside = TeleportFlushPolicy.Neighbourhood(0xFF01);
            Assert.AreEqual((ushort)0xFF01, outside[0]);
            Assert.IsFalse(outside.Skip(1).Any(lb => (lb >> 8) == 0xFF));
        }

        [TestMethod]
        public void Collides_OnlyAnotherCopyInsideTheDestination3x3()
        {
            var neighbourhood = TeleportFlushPolicy.Neighbourhood(0x0007);

            Assert.IsTrue(TeleportFlushPolicy.Collides(0x0007, Realm1, neighbourhood, Realm0), "same landblock id, other copy");
            Assert.IsTrue(TeleportFlushPolicy.Collides(0x0108, Realm1, neighbourhood, Realm0), "neighbour, other copy");
            Assert.IsFalse(TeleportFlushPolicy.Collides(0x0007, Realm0, neighbourhood, Realm0), "same copy never collides");
            Assert.IsFalse(TeleportFlushPolicy.Collides(0x0016, Realm1, neighbourhood, Realm0), "outside the 3x3");
        }
    }
}
