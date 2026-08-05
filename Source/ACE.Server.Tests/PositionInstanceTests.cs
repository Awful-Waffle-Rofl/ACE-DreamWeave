using ACE.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers the Position.Instance encoding introduced by Phase 1 of the ACRealms
    /// instancing port: [1 bit ephemeral][15 bits realmId][16 bits shortInstanceId].
    /// The roundtrip test is ported from ACRealms' RealmTests.Position_InstanceIDConversion.
    /// </summary>
    [TestClass]
    public class PositionInstanceTests
    {
        [TestMethod]
        public void ParseInstanceID_RoundTrips_EphemeralInstance()
        {
            // ported from ACRealms RealmTests.Position_InstanceIDConversion
            Position.ParseInstanceID(0x80020001, out var isEphemeral, out var realmId, out var shortInstanceId);

            Assert.IsTrue(isEphemeral);
            Assert.AreEqual((ushort)1, shortInstanceId);
            Assert.AreEqual((ushort)2, realmId);

            Assert.AreEqual(0x80020001, Position.InstanceIDFromVars(realmId, shortInstanceId, isEphemeral));
        }

        [TestMethod]
        public void ParseInstanceID_ZeroIsBaseWorld()
        {
            Position.ParseInstanceID(0, out var isEphemeral, out var realmId, out var shortInstanceId);

            Assert.IsFalse(isEphemeral);
            Assert.AreEqual((ushort)0, realmId);
            Assert.AreEqual((ushort)0, shortInstanceId);
        }

        [TestMethod]
        public void InstanceIDFromVars_RoundTrips_AllFieldBoundaries()
        {
            foreach (var realmId in new ushort[] { 0, 1, 0x7FFF })
            {
                foreach (var shortInstanceId in new ushort[] { 0, 1, 0xFFFF })
                {
                    foreach (var ephemeral in new[] { false, true })
                    {
                        var instance = Position.InstanceIDFromVars(realmId, shortInstanceId, ephemeral);
                        Position.ParseInstanceID(instance, out var parsedEphemeral, out var parsedRealmId, out var parsedShortInstanceId);

                        Assert.AreEqual(ephemeral, parsedEphemeral);
                        Assert.AreEqual(realmId, parsedRealmId);
                        Assert.AreEqual(shortInstanceId, parsedShortInstanceId);
                    }
                }
            }
        }

        [TestMethod]
        public void InstanceIDFromVars_RealmIdAboveRange_Throws()
        {
            Assert.ThrowsExactly<System.ArgumentOutOfRangeException>(() => Position.InstanceIDFromVars(0x8000, 0, false));
        }

        [TestMethod]
        public void RealmID_And_IsEphemeralRealm_ReadTheInstanceField()
        {
            var pos = new Position(0x7F7F001C, 84, 84, 80, 0, 0, 0, 1, Position.InstanceIDFromVars(5, 3, true));

            Assert.AreEqual((ushort)5, pos.RealmID);
            Assert.IsTrue(pos.IsEphemeralRealm);
        }

        [TestMethod]
        public void SetToDefaultRealmInstance_UsesShortInstanceZeroNonEphemeral()
        {
            var pos = new Position(0x7F7F001C, 84, 84, 80, 0, 0, 0, 1, 0);
            pos.SetToDefaultRealmInstance(7);

            Assert.AreEqual(0x00070000u, pos.Instance);
            Assert.AreEqual((ushort)7, pos.RealmID);
            Assert.IsFalse(pos.IsEphemeralRealm);
        }

        [TestMethod]
        public void CopyConstructor_PropagatesInstance()
        {
            var source = new Position(0x7F7F001C, 84, 84, 80, 0, 0, 0, 1, 0x00020001);

            var copy = new Position(source);
            Assert.AreEqual(source.Instance, copy.Instance);

            var reassigned = new Position(source, 0x00030001);
            Assert.AreEqual(0x00030001u, reassigned.Instance);
            Assert.AreEqual(source.Cell, reassigned.Cell);
        }

        [TestMethod]
        public void InFrontOf_PropagatesInstance()
        {
            var source = new Position(0x7F7F001C, 84, 84, 80, 0, 0, 0, 1, 0x00020001);

            var inFront = source.InFrontOf(1.0);

            Assert.AreEqual(source.Instance, inFront.Instance);
        }

        [TestMethod]
        public void InstancedLandblock_ComposesInstanceAndNormalizedLandblock()
        {
            // cell bits are normalized to 0xFFFF so every cell in a landblock shares the key
            var pos = new Position(0x7F7F001C, 84, 84, 80, 0, 0, 0, 1, 0x00020001);

            Assert.AreEqual(0x000200017F7FFFFFul, pos.InstancedLandblock);
            Assert.AreEqual(0x7F7Fu, pos.LandblockShort);

            var baseWorld = new Position(0x7F7F001C, 84, 84, 80, 0, 0, 0, 1, 0);
            Assert.AreEqual(0x000000007F7FFFFFul, baseWorld.InstancedLandblock);
            Assert.AreNotEqual(pos.InstancedLandblock, baseWorld.InstancedLandblock);
        }
    }
}
