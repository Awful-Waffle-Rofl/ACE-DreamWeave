using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// pack_type_follow: brute and swarm points each keep up with how fast their own type is cleared, so clearing a few
    /// brute packs quickly does not leave the brute points empty.
    /// </summary>
    [TestClass]
    public class FraysTypeFollowTests
    {
        [TestInitialize]
        public void Init() => FraysSettings.ResetCacheForTest();

        [TestMethod]
        public void TypeFollow_IsOnByDefault()
        {
            Assert.IsTrue(FraysSettings.Toggle(FraysSettings.PackTypeFollow));
        }

        [TestMethod]
        public void AtTheFloor_EachTypeRestsAsTheCombinedSupplyDid()
        {
            foreach (var heat in new[] { 0, 7, 15, 20 })
            {
                var floor = FraysRules.SpawnTargetAt(heat, 300);
                var combined = FraysRules.TypedRestScale(8, heat, 0, true, floor);
                var oneType = FraysRules.TypedRestScaleOne(4, heat, 0, true, FraysRules.TypeTarget(floor, 0));
                Assert.AreEqual(combined, oneType, 1e-9, $"Heat {heat}: half the points at half the target");
            }
        }

        [TestMethod]
        public void ThreeBrutesTwentyFiveSecondsApart_BringTheBruteRestUnderTheLoop()
        {
            // A camp at Heat 7: three brute points, one cleared every 25 s (a 75 s loop).
            var window = FraysSettings.Number(FraysSettings.PackTypeFollowSeconds);
            var work = FraysRules.TypedWorth(7, 0);
            double rate = 0;

            for (var k = 0; k < 3; k++)
                rate = FraysRules.ClearRate(rate, k == 0 ? 0 : 25, work, window);

            var target = FraysRules.TypeTarget(FraysRules.SpawnTargetAt(7, 464), FraysRules.WarmRate(rate, 50, window));
            var rest = FraysRules.PointRest(FraysPointKind.Normal, false) * FraysRules.TypedRestScaleOne(3, 7, 0, true, target);
            Assert.IsTrue(rest < 75, $"a brute point rests {rest:0} s, under the 75 s loop");
        }

        [TestMethod]
        public void AFastBruteClearer_GetsShorterBruteRests_InProportion()
        {
            var floor = FraysRules.SpawnTargetAt(7, 464);
            var share = FraysSettings.Number(FraysSettings.PackTypeSupply);
            var extra = FraysSettings.Number(FraysSettings.PackTypeFollowExtra);

            Assert.AreEqual(floor * share, FraysRules.TypeTarget(floor, 0), 1e-9, "no clears: the type's share of the floor");
            Assert.AreEqual(floor * share, FraysRules.TypeTarget(floor, floor * share / (1 + extra) / 2), 1e-9, "a slow clearer keeps the floor");

            var fast = floor * 1.5;   // standard creatures an hour of brutes alone
            Assert.AreEqual(fast * (1 + extra), FraysRules.TypeTarget(floor, fast), 1e-9, "what it clears plus the follow extra");

            var atFloor = FraysRules.TypedRestScaleOne(4, 7, 0, true, FraysRules.TypeTarget(floor, 0));
            var following = FraysRules.TypedRestScaleOne(4, 7, 0, true, FraysRules.TypeTarget(floor, fast));
            Assert.AreEqual(atFloor * floor * share / (fast * (1 + extra)), following, 1e-9, "rest shrinks as the target grows");
        }
    }
}
