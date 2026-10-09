using ACE.Entity;
using ACE.Server.WorldObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Corpse.IsNoDropLocation is the single gate behind Corpse.IsOnNoDropLandblock, which
    /// Player.CalculateDeathItems consults before dropping anything. These pin the rule that
    /// every ephemeral instance is no-drop, regardless of landblock.
    /// </summary>
    [TestClass]
    public class CorpseNoDropTests
    {
        // First Pin, where the 2026-08-30 loss happened - NOT on the retail no-drop list
        private const uint FirstPinCell = 0x00430123;

        // Position.ParseInstanceID: bit 31 = ephemeral, bits 16-30 = realm, low 16 = short instance id
        private const uint EphemeralRealm1Instance = 0x80000000u | (1u << 16) | 0x1234u;

        private static Position At(uint cell, uint instance)
        {
            return new Position(cell, 54.68f, -382.24f, -17.99f, 0f, 0f, 0.13f, 0.99f, instance);
        }

        [TestMethod]
        public void NullLocation_IsNotNoDrop()
        {
            Assert.IsFalse(Corpse.IsNoDropLocation(null));
        }

        [TestMethod]
        public void OrdinaryLandblock_BaseInstance_Drops()
        {
            var pos = At(FirstPinCell, 0);

            Assert.IsFalse(pos.IsEphemeralRealm, "sanity: instance 0 is not ephemeral");
            Assert.IsFalse(Corpse.IsNoDropLocation(pos));
        }

        [TestMethod]
        public void OrdinaryLandblock_PersistentRealmInstance_Drops()
        {
            // realm 1 (Weave), persistent copy - ephemeral bit clear
            var pos = At(FirstPinCell, 1u << 16);

            Assert.IsFalse(pos.IsEphemeralRealm, "sanity: a persistent realm instance is not ephemeral");
            Assert.IsFalse(Corpse.IsNoDropLocation(pos));
        }

        [TestMethod]
        public void SameLandblock_EphemeralInstance_IsNoDrop()
        {
            var pos = At(FirstPinCell, EphemeralRealm1Instance);

            Assert.IsTrue(pos.IsEphemeralRealm, "sanity: ephemeral bit set");
            Assert.IsTrue(Corpse.IsNoDropLocation(pos));
        }

        [TestMethod]
        public void RetailNoDropLandblock_StillNoDrop()
        {
            Assert.IsTrue(Corpse.NoDrop_Landblocks.Count > 0, "sanity: retail list is populated");

            uint landblock = 0;
            foreach (var lb in Corpse.NoDrop_Landblocks) { landblock = lb; break; }

            var pos = At((landblock << 16) | 0x0100u, 0);

            Assert.IsTrue(Corpse.IsNoDropLocation(pos));
        }
    }
}
