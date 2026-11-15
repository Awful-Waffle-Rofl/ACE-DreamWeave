using System.Collections.Generic;
using System.Linq;

using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>After a death fade the faded points refill at once, so the camp is not left with few creatures to kill.</summary>
    [TestClass]
    public class FraysDeathRefillTests
    {
        [TestMethod]
        public void DeathRefill_EachFadedPointOnce_ByItsCamper()
        {
            // a death at Heat 7: 28 creatures over 8 packs faded (several creatures a point)
            var faded = new List<(uint, int)>();
            foreach (var point in new[] { 0, 2, 3, 5, 6, 8, 9, 11 })
                for (var k = 0; k < (point % 3) + 2; k++)
                    faded.Add((7u, point));

            var refill = FraysRules.DeathRefillPoints(faded, 12);
            CollectionAssert.AreEqual(new[] { 0, 2, 3, 5, 6, 8, 9, 11 }, refill.Select(r => r.Index).ToArray(), "every faded point, once");
            Assert.IsTrue(refill.All(r => r.Owner == 7u));
        }

        [TestMethod]
        public void DeathRefill_GroupCamp_KeepsEachStream_AndDropsBadPoints()
        {
            var refill = FraysRules.DeathRefillPoints(new[] { (2u, 1), (1u, 1), (1u, 4), (1u, -1), (2u, 12) }, 12);
            CollectionAssert.AreEqual(new[] { (1u, 1), (1u, 4), (2u, 1) }, refill.ToArray());
            Assert.AreEqual(0, FraysRules.DeathRefillPoints(null, 12).Count);
        }

        [TestMethod]
        public void DeathRefill_IsWiredIntoTheFire()
        {
            // The fire notes the faded points and refills them at the cooled Heat before the ordinary stream.
            var src = System.IO.File.ReadAllText(FindFire());
            StringAssert.Contains(src, "FraysRules.DeathRefillPoints(fade.Select(");
            StringAssert.Contains(src, "RefillAfterDeath(c, now, present);");
        }

        private static string FindFire()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);

            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ACE.Server", "Frays", "FraysFire.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "ACE.Server/Frays/FraysFire.cs not found above the test folder");
            return System.IO.Path.Combine(dir.FullName, "ACE.Server", "Frays", "FraysFire.cs");
        }
    }
}
