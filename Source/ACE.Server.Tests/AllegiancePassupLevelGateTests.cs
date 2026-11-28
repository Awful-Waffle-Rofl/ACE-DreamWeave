using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// DreamWeave: the retail passup level gate (a vassal who swore while out-leveling their patron
    /// passes nothing until the patron catches up) is skipped when the patron is above level 200.
    /// The rule is checked directly, and both live readers - the passup itself and the allegiance
    /// panel's MayPassupExperience bit - are pinned to it so they cannot drift apart.
    /// </summary>
    [TestClass]
    public class AllegiancePassupLevelGateTests
    {
        [TestMethod]
        public void ExemptThreshold_IsAboveLevel200()
        {
            Assert.AreEqual(200, AllegianceManager.PassupLevelGateExemptAboveLevel);
        }

        [TestMethod]
        public void UngatedVassal_PassesUp_AtAnyPatronLevel()
        {
            Assert.IsTrue(AllegianceManager.VassalPassesUp(true, 1));
            Assert.IsTrue(AllegianceManager.VassalPassesUp(true, 200));
            Assert.IsTrue(AllegianceManager.VassalPassesUp(true, 201));
            Assert.IsTrue(AllegianceManager.VassalPassesUp(true, null));
        }

        [TestMethod]
        public void GatedVassal_IsBlocked_WhenPatronIsAtOrBelow200()
        {
            Assert.IsFalse(AllegianceManager.VassalPassesUp(false, 1));
            Assert.IsFalse(AllegianceManager.VassalPassesUp(false, 199));
            Assert.IsFalse(AllegianceManager.VassalPassesUp(false, 200));
            Assert.IsFalse(AllegianceManager.VassalPassesUp(false, null));
        }

        [TestMethod]
        public void GatedVassal_PassesUp_WhenPatronIsAbove200()
        {
            Assert.IsTrue(AllegianceManager.VassalPassesUp(false, 201));
            Assert.IsTrue(AllegianceManager.VassalPassesUp(false, 275));
            Assert.IsTrue(AllegianceManager.VassalPassesUp(false, 999));
        }

        [TestMethod]
        public void DoPassXP_GatesThroughVassalPassesUp_WithThePatronsLevel()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Managers/AllegianceManager.cs"),
                "private static void DoPassXP(AllegianceNode vassalNode, ulong amount)");

            StringAssert.Contains(body, "if (!VassalPassesUp(vassal.ExistedBeforeAllegianceXpChanges, patronNode.Player?.Level))");
            Assert.AreEqual(-1, body.IndexOf("if (!vassal.ExistedBeforeAllegianceXpChanges)", StringComparison.Ordinal), "the bare retail gate must not return");
        }

        [TestMethod]
        public void AllegiancePanel_PassupBit_UsesTheSameRule()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Network/Structure/AllegianceData.cs");

            StringAssert.Contains(source, "AllegianceManager.VassalPassesUp(node.Player.ExistedBeforeAllegianceXpChanges, node.Patron?.Player.Level)");
        }
    }
}
