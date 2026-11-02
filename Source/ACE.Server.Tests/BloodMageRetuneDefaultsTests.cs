using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the shipped code defaults of the 2026-10-17 Blood Mage retune: Weakened Blood slow 10/20/30%
    /// and Exsanguinate charges kept 2/3/4. Reads DefaultPropertyManager's immutable default tables and
    /// never touches the PropertyManager caches, so it needs no seeding and passes alone or in any order.
    /// </summary>
    [TestClass]
    public class BloodMageRetuneDefaultsTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestMethod]
        public void WeakenedBloodSlow_ShipsAt10_20_30Percent()
        {
            var expected = new[] { 0.10, 0.20, 0.30 };

            for (var i = 0; i < 3; i++)
            {
                var key = $"class_ability_weakenedblood_slow_r{i + 1}";
                Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(key), key);
                Assert.AreEqual(expected[i], DefaultPropertyManager.DefaultDoubleProperties[key].Item, 1e-9, key);
            }

            Assert.AreEqual(0.50, DefaultPropertyManager.DefaultDoubleProperties["class_ability_weakenedblood_slow_floor"].Item, 1e-9,
                "the floor is unchanged by the retune");
        }

        [TestMethod]
        public void ExsanguinateChargesKept_ShipsAt2_3_4()
        {
            var expected = new long[] { 2, 3, 4 };

            for (var i = 0; i < 3; i++)
            {
                var key = $"class_ability_exsanguinate_charges_kept_r{i + 1}";
                Assert.IsTrue(DefaultPropertyManager.DefaultLongProperties.ContainsKey(key), key);
                Assert.AreEqual(expected[i], DefaultPropertyManager.DefaultLongProperties[key].Item, key);
            }
        }
    }
}