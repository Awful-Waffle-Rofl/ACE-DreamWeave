using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers ClothingHumanFallback.Resolve, the pure mesh-identity comparison. The dat-backed
    /// overload is not unit-testable (reads client_portal.dat).
    /// </summary>
    [TestClass]
    public class ClothingHumanFallbackTests
    {
        private const uint Wearer = 0x0200196E;
        private const uint HumanMale = (uint)SetupConst.HumanMale;
        private const uint HumanFemale = (uint)SetupConst.HumanFemale;

        private static List<uint> Parts(int count, uint seed = 100) => Enumerable.Range(0, count).Select(i => seed + (uint)i).ToList();

        private static uint? Run(List<uint> wearer, List<uint> male, List<uint> female, params uint[] tableKeys)
        {
            var map = new Dictionary<uint, List<uint>> { [Wearer] = wearer, [HumanMale] = male, [HumanFemale] = female };
            return ClothingHumanFallback.Resolve(Wearer, id => map[id], tableKeys.Contains);
        }

        [TestMethod]
        public void IdenticalParts_TableHasHuman_ReturnsHumanId()
        {
            Assert.AreEqual((uint?)HumanMale, Run(Parts(17), Parts(17), Parts(17, 500), HumanMale, HumanFemale));
        }

        [TestMethod]
        public void IdenticalToFemale_ReturnsFemaleId()
        {
            Assert.AreEqual((uint?)HumanFemale, Run(Parts(17, 500), Parts(17), Parts(17, 500), HumanMale, HumanFemale));
        }

        [TestMethod]
        public void TableLacksHumanKey_ReturnsNull()
        {
            Assert.IsNull(Run(Parts(17), Parts(17), Parts(17, 500), 0x02000099));
        }

        [TestMethod]
        public void OneDifferingPartWithinBody_ReturnsNull()
        {
            var wearer = Parts(17);
            wearer[5] = 9999;
            Assert.IsNull(Run(wearer, Parts(17), Parts(17, 500), HumanMale, HumanFemale));
        }

        [TestMethod]
        public void DifferenceOnlyAtHeadPart_StillMatches()
        {
            var wearer = Parts(17);
            wearer[16] = 9999;
            Assert.AreEqual((uint?)HumanMale, Run(wearer, Parts(17), Parts(17, 500), HumanMale, HumanFemale));
        }

        [TestMethod]
        public void WearerFewerThanSixteenParts_ReturnsNull()
        {
            Assert.IsNull(Run(Parts(15), Parts(17), Parts(17, 500), HumanMale, HumanFemale));
        }

        [TestMethod]
        public void HumanFewerThanSixteenParts_ReturnsNull()
        {
            Assert.IsNull(Run(Parts(17), Parts(15), Parts(15, 500), HumanMale, HumanFemale));
        }

        [TestMethod]
        public void ShortHumanMale_LoopContinuesToMatchingFemale()
        {
            Assert.AreEqual((uint?)HumanFemale, Run(Parts(17), Parts(15), Parts(17), HumanMale, HumanFemale));
        }

        private const uint Table = 0x02000001;
        private const uint ThisSetup = 0x0200196E;

        private static uint? Pick(uint[] has, uint? fb, out int fbCalls)
        {
            var calls = 0;
            var r = ClothingHumanFallback.PickSetupId(Table, ThisSetup, has.Contains, _ => { calls++; return fb; });
            fbCalls = calls;
            return r;
        }

        [TestMethod]
        public void Pick_BothPresent_ReturnsSetupTableId_NoFallbackCall()
        {
            Assert.AreEqual((uint?)Table, Pick(new[] { Table, ThisSetup }, HumanFemale, out var calls));
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public void Pick_OnlyThisSetup_ReturnsThisSetup_NoFallbackCall()
        {
            Assert.AreEqual((uint?)ThisSetup, Pick(new[] { ThisSetup }, HumanFemale, out var calls));
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public void Pick_Neither_FallbackHit_ReturnsFallback()
        {
            Assert.AreEqual((uint?)HumanFemale, Pick(new[] { HumanFemale }, HumanFemale, out var calls));
            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public void Pick_Neither_NoFallback_ReturnsNull()
        {
            Assert.IsNull(Pick(new uint[0], null, out _));
        }

        [TestMethod]
        public void Pick_FallbackNotInTable_ReturnsNull()
        {
            Assert.IsNull(Pick(new uint[0], HumanFemale, out _));
        }
    }
}
