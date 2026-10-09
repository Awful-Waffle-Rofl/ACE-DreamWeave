using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the /mods summary line format (EquipmentModCommands.FormatSummaryLine), exercised as a pure
    /// function against real registry rows rather than through a live session - singular/plural item counts,
    /// the "capped" note, and the "[inactive - requires X]" note for a non-standalone mod the player has not
    /// learned.
    /// </summary>
    [TestClass]
    public class EquipmentModCommandTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestMethod]
        public void FormatSummaryLine_SingularItemNoCapNoInactive()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);
            var appliedValue = EquipmentModValue.Resolve(deadeye, 0.5);

            var line = EquipmentModCommands.FormatSummaryLine(deadeye, appliedValue, 1, 0.5, false, null);

            Assert.AreEqual($"- Deadeye: {deadeye.Format(appliedValue)} (1 item, 50% of one perfect roll)", line);
        }

        [TestMethod]
        public void FormatSummaryLine_PluralItemsAndOverOneHundredPercent()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);
            var appliedValue = EquipmentModValue.Resolve(deadeye, 1.6);

            var line = EquipmentModCommands.FormatSummaryLine(deadeye, appliedValue, 2, 1.6, false, null);

            Assert.AreEqual($"- Deadeye: {deadeye.Format(appliedValue)} (2 items, 160% of one perfect roll)", line);
        }

        [TestMethod]
        public void FormatSummaryLine_CappedNoteAppendedWhenCapped()
        {
            var acidProc = EquipmentModRegistry.Get(EquipmentModId.AcidProc);
            var appliedValue = EquipmentModValue.Resolve(acidProc, acidProc.StackCap);

            var line = EquipmentModCommands.FormatSummaryLine(acidProc, appliedValue, 4, acidProc.StackCap, true, null);

            Assert.AreEqual($"- Acid Proc: {acidProc.Format(appliedValue)} (4 items, 300% of one perfect roll, capped)", line);
        }

        [TestMethod]
        public void FormatSummaryLine_InactiveNoteAppendedForUnlearnedNonStandaloneMod()
        {
            var acidProc = EquipmentModRegistry.Get(EquipmentModId.AcidProc);
            var appliedValue = EquipmentModValue.Resolve(acidProc, 0.3);

            var line = EquipmentModCommands.FormatSummaryLine(acidProc, appliedValue, 1, 0.3, false, "Acid Proc");

            Assert.AreEqual($"- Acid Proc: {acidProc.Format(appliedValue)} (1 item, 30% of one perfect roll) [inactive - requires Acid Proc]", line);
        }
    }
}
