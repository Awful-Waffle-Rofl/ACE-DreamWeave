using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.EquipmentMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Golden pins for the two no-behaviour-change extractions made for the ceiling probes. The expected values come
    /// from the pre-extraction inline code: Creature.GetEquippedModPotencySum returned
    /// <c>StackCap &gt; 0 ? Math.Min(sum, StackCap) : sum</c>, and AddKillerInstinctOpening did
    /// <c>if (stacks &lt; cap) stacks++</c>.
    /// </summary>
    [TestClass]
    public class CeilingExtractionGoldenTests
    {
        [TestMethod]
        public void CapPotencySum_MatchesTheOldInlineCap()
        {
            Assert.AreEqual(5.0, EquipmentModValue.CapPotencySum(EquipmentModRegistry.Get(EquipmentModId.Harmonics), 5.0), "StackCap 0 passes the sum through");
            Assert.AreEqual(1.5, EquipmentModValue.CapPotencySum(EquipmentModRegistry.Get(EquipmentModId.AcidProc), 1.5), "under the cap passes through");
            Assert.AreEqual(3.0, EquipmentModValue.CapPotencySum(EquipmentModRegistry.Get(EquipmentModId.AcidProc), 99.0), "over the cap is clipped to StackCap");
        }

        [TestMethod]
        public void NextKillerInstinctStacks_MatchesTheOldInlineCap()
        {
            Assert.AreEqual(5, Player.NextKillerInstinctStacks(5, 3), "already over the cap: unchanged, never pulled down");
            Assert.AreEqual(3, Player.NextKillerInstinctStacks(2, 3), "one below the cap: grows by one");
            Assert.AreEqual(3, Player.NextKillerInstinctStacks(3, 3), "at the cap: unchanged");
        }
    }
}
