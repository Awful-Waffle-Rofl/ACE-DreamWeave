using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// CombatPetRetireRules.ShouldRetireActivePets: whether using a combat-pet essence should retire the
    /// owner's active combat pet(s) and take their slot(s), instead of refusing with "is already active" or
    /// silently adding a second pet alongside the first.
    ///
    /// SCOPE NOTE: no test in this project can construct a live Player or Pet, so this covers only the pure
    /// isFirstSummonOfActivation/slot-populated logic - the same limit as PetLifespanSyncTests. Which pets
    /// actually get queued for dismissal, and that the retirement only fires once Pet.Init's placement has
    /// actually succeeded, need an in-game check.
    /// </summary>
    [TestClass]
    public class CombatPetRetireRulesTests
    {
        private const uint FirePet = 48887;
        private const uint AcidPet = 49052;

        [TestMethod]
        public void FirstSummon_DifferentEssence_SinglePetActive_Retires()
        {
            // one fire pet out, no Summon 2x, acid essence used as the first (only) summon of the activation
            Assert.IsTrue(CombatPetRetireRules.ShouldRetireActivePets(true, FirePet, null));
        }

        [TestMethod]
        public void FirstSummon_SameEssence_SinglePetActive_Retires()
        {
            // owner ruling (2026-09-23): re-using the SAME essence while its own pet is still out now
            // retires and resummons, rather than refusing "is already active" as the old #1129 rule did -
            // this test would FAIL under the old ShouldSwap rule, which refused on a matching wcid
            Assert.IsTrue(CombatPetRetireRules.ShouldRetireActivePets(true, FirePet, null));
        }

        [TestMethod]
        public void FirstSummon_SameEssence_SecondarySlot_Retires()
        {
            // same case, but the live pet is sitting in the secondary (Summon 2x) slot instead of the primary -
            // would also FAIL under the old rule, which refused a match in either slot
            Assert.IsTrue(CombatPetRetireRules.ShouldRetireActivePets(true, null, FirePet));
        }

        [TestMethod]
        public void FirstSummon_BothSlotsActive_Retires()
        {
            // Summon 2x fire+fire out, either essence used again as the first summon of a fresh activation
            Assert.IsTrue(CombatPetRetireRules.ShouldRetireActivePets(true, FirePet, FirePet));
        }

        [TestMethod]
        public void FirstSummon_MixedPairActive_Retires()
        {
            // Summon 2x fire+acid out - still retires regardless of which essence is used next
            Assert.IsTrue(CombatPetRetireRules.ShouldRetireActivePets(true, FirePet, AcidPet));
        }

        [TestMethod]
        public void FirstSummon_NoActiveCombatPets_DoesNotRetire()
        {
            // both slots null - nothing out to retire; callers only reach this when a combat-pet essence is
            // being used, but the rule itself must still be safe if that invariant ever lapses
            Assert.IsFalse(CombatPetRetireRules.ShouldRetireActivePets(true, null, null));
        }

        [TestMethod]
        public void SecondSummonOfActivation_NeverRetires_EvenWithBothSlotsActive()
        {
            // the SECOND summon of a Summon 2x activation (Init's spawnStagger == true) must never retire the
            // pet the SAME activation just placed in the primary slot a moment ago - this test would FAIL
            // against a rule that only looked at slot occupancy and ignored which summon of the activation
            // this is
            Assert.IsFalse(CombatPetRetireRules.ShouldRetireActivePets(false, FirePet, null));
        }

        [TestMethod]
        public void SecondSummonOfActivation_NeverRetires_EvenWhenEmpty()
        {
            // sanity: the second-summon gate alone is enough to decline, regardless of slot state
            Assert.IsFalse(CombatPetRetireRules.ShouldRetireActivePets(false, null, null));
        }
    }
}
