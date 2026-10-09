using System;
using System.IO;
using ACE.Server.WorldObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Player.FastTick is FastTickPolicy.Resolve(IsPKType, FastTickOptIn, AllPlayers). The seam exists so the per-tick
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

        [DataTestMethod]
        // isPk, serverFlag, optIn, expected
        [DataRow(false, false, null, false)]
        [DataRow(false, true, null, true)]
        [DataRow(false, false, true, true)]
        [DataRow(false, true, true, true)]
        [DataRow(false, false, false, false)]
        [DataRow(false, true, false, false)]
        [DataRow(true, false, null, true)]
        [DataRow(true, true, null, true)]
        [DataRow(true, false, true, true)]
        [DataRow(true, true, true, true)]
        [DataRow(true, false, false, true)]
        [DataRow(true, true, false, true)]
        public void Resolve_TruthTable(bool isPk, bool serverFlag, bool? optIn, bool expected)
        {
            Assert.AreEqual(expected, FastTickPolicy.Resolve(isPk, optIn, () => serverFlag));
        }

        [TestMethod]
        public void Resolve_OptOutBeatsServerFlag()
        {
            Assert.IsFalse(FastTickPolicy.Resolve(false, false, () => true));
        }

        [TestMethod]
        public void Resolve_OptInBeatsServerOff()
        {
            Assert.IsTrue(FastTickPolicy.Resolve(false, true, () => false));
        }

        [TestMethod]
        public void Resolve_PkIgnoresOptOut()
        {
            Assert.IsTrue(FastTickPolicy.Resolve(true, false, () => false));
        }

        [DataTestMethod]
        [DataRow(false, true, false)]
        [DataRow(false, false, false)]
        [DataRow(true, null, false)]
        [DataRow(true, true, false)]
        [DataRow(true, false, false)]
        [DataRow(false, null, true)]
        public void Resolve_ConsultsServerFlagOnlyWhenNoChoiceAndNotPk(bool isPk, bool? optIn, bool expectCalled)
        {
            var calls = 0;
            FastTickPolicy.Resolve(isPk, optIn, () => { calls++; return true; });
            Assert.AreEqual(expectCalled ? 1 : 0, calls);
        }

        [TestMethod]
        public void Resolve_WithDefaultSeamAndNoShardConfig_DoesNotThrow()
        {
            // optIn null reaches the real seam, which swallows the PropertyManager throw to false.
            Assert.IsFalse(FastTickPolicy.Resolve(false, null, FastTickPolicy.AllPlayers));
            Assert.IsTrue(FastTickPolicy.Resolve(false, true, FastTickPolicy.AllPlayers));
        }

        [TestMethod]
        public void PlayerTick_FastTickProperty_BindsResolveWithOptIn()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string path = null;
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", "Player_Tick.cs");
                if (File.Exists(candidate)) { path = candidate; break; }
                dir = dir.Parent;
            }
            Assert.IsNotNull(path, "could not find Player_Tick.cs by walking up from the test base directory");

            var src = File.ReadAllText(path);
            StringAssert.Contains(src,
                "public bool FastTick => FastTickPolicy.Resolve(IsPKType, FastTickOptIn, FastTickPolicy.AllPlayers);");
        }
    }
}
