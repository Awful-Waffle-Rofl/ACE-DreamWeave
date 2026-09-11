using System;
using ACE.Server.WorldObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Player.FastTick is IsPKType || FastTickAllPlayers(). The seam exists so the per-tick
    /// path never throws where PropertyManager has no shard config (this test project).
    /// </summary>
    [TestClass]
    public class FastTickAllPlayersTests
    {
        [TestMethod]
        public void DefaultSeam_WithoutShardConfig_ReturnsFalseAndDoesNotThrow()
        {
            // PropertyManager reads throw in this project; the seam must swallow that to false.
            Assert.IsFalse(FastTickPolicy.AllPlayers());
        }

        [TestMethod]
        public void Seam_IsSwappableAndRestorable()
        {
            var original = FastTickPolicy.AllPlayers;
            try
            {
                FastTickPolicy.AllPlayers = () => true;
                Assert.IsTrue(FastTickPolicy.AllPlayers());
            }
            finally
            {
                FastTickPolicy.AllPlayers = original;
            }

            Assert.IsFalse(FastTickPolicy.AllPlayers());
        }
    }
}
