using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// PvpModes wiring: every mode's MapPool is both arena maps, and each map can seat the mode's largest
    /// possible match from the set keyed by that mode's ModeKey.
    /// </summary>
    [TestClass]
    public class PvpModesMapPoolTests
    {
        [TestMethod]
        public void EveryMode_PoolsBothArenaMaps()
        {
            foreach (var mode in PvpModes.All(PvpTunables.Defaults))
                CollectionAssert.AreEqual(new[] { "arena_0066", "arena_0067" }, mode.MapPool.Select(m => m.MapKey).ToArray(), mode.ModeKey);
        }

        [TestMethod]
        public void EveryPooledMap_CanSeatTheModesLargestMatch()
        {
            foreach (var mode in PvpModes.All(PvpTunables.Defaults))
            {
                var largest = mode.TeamCountRange.Max * mode.TeamSize;

                foreach (var map in mode.MapPool)
                    Assert.IsTrue(map.SpawnPointsFor(mode.ModeKey).Count >= largest, $"{mode.ModeKey} on {map.MapKey}: {map.SpawnPointsFor(mode.ModeKey).Count} points for {largest} players");
            }
        }

        [TestMethod]
        public void ModeKeys_MatchTheCatalogKeys()
        {
            Assert.AreEqual(ArenaMapCatalog.OneVOneKey, PvpModes.OneVOne(PvpTunables.Defaults).ModeKey);
            Assert.AreEqual(ArenaMapCatalog.TwoVTwoKey, PvpModes.TwoVTwo(PvpTunables.Defaults).ModeKey);
            Assert.AreEqual(ArenaMapCatalog.FfaKey, PvpModes.Ffa(PvpTunables.Defaults).ModeKey);
            Assert.AreEqual("1v1", ArenaMapCatalog.OneVOneKey);
            Assert.AreEqual("2v2", ArenaMapCatalog.TwoVTwoKey);
            Assert.AreEqual("ffa", ArenaMapCatalog.FfaKey);
        }
    }
}
