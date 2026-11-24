using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.EquipmentMods;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The untrained-wearer requirement suffix on equipment-mod appraisal and Salvage Roller lines. Assertions are
    /// on the rendered text, the thing a player reads.
    /// </summary>
    [TestClass]
    public class EquipmentModDisplayTests
    {
        [TestMethod]
        public void AbilityRequirementLine_StandaloneVsMachinery_ManaBarrierRequires()
        {
            Assert.AreEqual("Works without the ability", EquipmentModDisplay.AbilityRequirement(EquipmentModRegistry.Get(EquipmentModId.Deadeye)));
            Assert.AreEqual("Requires Acid Proc trained", EquipmentModDisplay.AbilityRequirement(EquipmentModRegistry.Get(EquipmentModId.AcidProc)));
            Assert.AreEqual("Requires Mana Barrier trained", EquipmentModDisplay.AbilityRequirement(EquipmentModRegistry.Get(EquipmentModId.ManaBarrier)));

            var resonance = EquipmentModRegistry.Get(EquipmentModId.Resonance);
            Assert.IsTrue(ClassAbilityRegistry.Abilities.TryGetValue(resonance.LinkedAbility, out var spellAoe), "control: Resonance links a registered ability");
            Assert.AreEqual($"Requires {spellAoe.DisplayName} trained", EquipmentModDisplay.AbilityRequirement(resonance));
        }

        [TestMethod]
        public void AbilityRequirement_UnregisteredLink_FallsBackToTheModName()
        {
            var orphan = new EquipmentModDefinition { DisplayName = "Orphan", LinkedAbility = (ClassAbilityId)65000, Standalone = false };

            Assert.AreEqual("Requires Orphan trained", EquipmentModDisplay.AbilityRequirement(orphan));
        }

        [TestMethod]
        public void EveryRegisteredMod_RendersTheRequirementItsStandaloneFlagImplies()
        {
            var standalone = 0;
            var machinery = 0;

            foreach (var d in EquipmentModRegistry.AllMods)
            {
                if (d.Standalone)
                {
                    standalone++;
                    Assert.AreEqual("Works without the ability", EquipmentModDisplay.AbilityRequirement(d), d.DisplayName);
                }
                else
                {
                    machinery++;
                    // the indexer on purpose: a registered mod linking an unregistered ability must fail loudly here
                    Assert.AreEqual($"Requires {ClassAbilityRegistry.Abilities[d.LinkedAbility].DisplayName} trained", EquipmentModDisplay.AbilityRequirement(d), d.DisplayName);
                }
            }

            Assert.AreEqual(15, standalone, "standalone mods");
            Assert.AreEqual(33, machinery, "machinery mods");
        }
    }
}
