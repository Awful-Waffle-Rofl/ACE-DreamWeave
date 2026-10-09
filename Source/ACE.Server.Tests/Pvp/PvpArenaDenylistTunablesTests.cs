using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// pvp_arena_denylist (Docs/Pvp/DESIGN.md "Joining the queue"), ported from Doctide `arenas_blacklist`: a
    /// CSV of character ids parsed and cached once per DialSource call, following PvpSafeZoneTunables exactly.
    /// </summary>
    [TestClass]
    public class PvpArenaDenylistTunablesTests
    {
        [TestMethod]
        public void ParseCsv_Empty_IsEmptySet()
        {
            Assert.AreEqual(0, PvpArenaDenylistTunables.ParseCsv("").Count);
            Assert.AreEqual(0, PvpArenaDenylistTunables.ParseCsv(null).Count);
            Assert.AreEqual(0, PvpArenaDenylistTunables.ParseCsv("   ").Count);
        }

        [TestMethod]
        public void ParseCsv_ParsesAndTrims()
        {
            var ids = PvpArenaDenylistTunables.ParseCsv(" 100, 200 ,300");

            CollectionAssert.AreEquivalent(new uint[] { 100, 200, 300 }, ids.ToArray());
        }

        [TestMethod]
        public void ParseCsv_SkipsUnparseableTokens()
        {
            var ids = PvpArenaDenylistTunables.ParseCsv("100,notanid,,200");

            CollectionAssert.AreEquivalent(new uint[] { 100, 200 }, ids.ToArray());
        }

        [TestMethod]
        public void DialSource_InTestEnvironment_FallsBackToDefaults()
        {
            var dials = PvpArenaDenylistTunables.DialSource();

            Assert.AreEqual(0, dials.DenylistedCharacterIds.Count);
        }

        /// <summary>DISCRIMINATES: IsDenylisted reflects the resolved set, not just the default.</summary>
        [TestMethod]
        public void IsDenylisted_ReflectsResolvedSet()
        {
            var saved = PvpArenaDenylistTunables.DialSource;

            try
            {
                PvpArenaDenylistTunables.DialSource = () => new PvpArenaDenylistDials(PvpArenaDenylistTunables.ParseCsv("42,99"));

                Assert.IsTrue(PvpArenaDenylistTunables.IsDenylisted(42));
                Assert.IsTrue(PvpArenaDenylistTunables.IsDenylisted(99));
                Assert.IsFalse(PvpArenaDenylistTunables.IsDenylisted(1));
            }
            finally
            {
                PvpArenaDenylistTunables.DialSource = saved;
            }
        }
    }
}
