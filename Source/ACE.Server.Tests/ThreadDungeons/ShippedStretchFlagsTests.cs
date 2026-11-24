using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>Pins the owner's 2026-11-18 data rulings on the shipped species tables.</summary>
    [TestClass]
    public class ShippedStretchFlagsTests
    {
        [TestMethod]
        public void Shipped_tables_carry_exactly_the_owner_flags()
        {
            var tables = ShippedRosterFixture.Tables;
            Assert.IsNotNull(tables, "shipped species tables not found");

            CollectionAssert.AreEquivalent(
                new[] { "chicken", "rabbit", "scarecrow" },
                tables.Values.Where(t => !t.Stretches).Select(t => t.Id).ToList());
            Assert.AreEqual(1.25, tables["snowman"].Overscale);
            Assert.AreEqual(1, tables.Values.Count(t => t.Overscale != 1.0));
            Assert.IsTrue(tables["doll"].Stretches);
        }
    }
}
