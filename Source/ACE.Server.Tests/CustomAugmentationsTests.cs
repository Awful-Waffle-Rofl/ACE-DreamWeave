using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Custom Dreamweave Augmentations pure catalog and Fibonacci pricing rule
    /// (ACE.Server/Entity/CustomAugmentations.cs), plus the AugmentationDevice dictionary wiring
    /// (MaxAugs/AugProps) it depends on.
    /// </summary>
    [TestClass]
    public class CustomAugmentationsTests
    {
        /// <summary>
        /// The first 12 costs, at a ceiling well above 11, match the Fibonacci sequence starting
        /// 1, 1, 2, 3, 5, ...
        /// </summary>
        [DataTestMethod]
        [DataRow(0, 1L)]
        [DataRow(1, 1L)]
        [DataRow(2, 2L)]
        [DataRow(3, 3L)]
        [DataRow(4, 5L)]
        [DataRow(5, 8L)]
        [DataRow(6, 13L)]
        [DataRow(7, 21L)]
        [DataRow(8, 34L)]
        [DataRow(9, 55L)]
        [DataRow(10, 89L)]
        [DataRow(11, 144L)]
        public void CostFor_FirstTwelve_MatchesFibonacci(int owned, long expected)
        {
            Assert.AreEqual(expected, CustomAugmentations.CostFor(owned, 100));
        }

        /// <summary>
        /// The index clamp holds: an owned count beyond the ceiling yields the same price as the
        /// ceiling itself - a flat plateau, not further growth.
        /// </summary>
        [TestMethod]
        public void CostFor_CeilingClamp_Plateaus()
        {
            Assert.AreEqual(CustomAugmentations.CostFor(20, 20), CustomAugmentations.CostFor(50, 20));
        }

        /// <summary>
        /// A negative owned count is clamped to 0, returning cost(0) = 1.
        /// </summary>
        [TestMethod]
        public void CostFor_NegativeOwned_ReturnsOne()
        {
            Assert.AreEqual(1L, CustomAugmentations.CostFor(-5, 20));
        }

        /// <summary>
        /// The result never exceeds int.MaxValue, even well past the point where the raw Fibonacci
        /// value would overflow a 32-bit int (fib(47) already does).
        /// </summary>
        [TestMethod]
        public void CostFor_NeverExceedsIntMaxValue()
        {
            for (var owned = 0; owned <= 100; owned++)
            {
                var cost = CustomAugmentations.CostFor(owned, 60);
                Assert.IsTrue(cost <= int.MaxValue, $"CostFor({owned}, 60) = {cost} exceeded int.MaxValue");
            }
        }

        /// <summary>
        /// Every catalog key is wired into both AugmentationDevice dictionaries, and MaxAugs reports
        /// int.MaxValue for each - a future edit cannot silently cap them without this test noticing.
        /// </summary>
        [TestMethod]
        public void Catalog_KeysWiredIntoAugmentationDevice()
        {
            foreach (var type in CustomAugmentations.Catalog.Keys)
            {
                Assert.IsTrue(AugmentationDevice.MaxAugs.ContainsKey(type), $"{type} missing from AugmentationDevice.MaxAugs");
                Assert.IsTrue(AugmentationDevice.AugProps.ContainsKey(type), $"{type} missing from AugmentationDevice.AugProps");
                Assert.AreEqual(int.MaxValue, AugmentationDevice.MaxAugs[type], $"{type} is not uncapped in AugmentationDevice.MaxAugs");
            }
        }
    }

    /// <summary>
    /// The spell-duration half of Fi's Lingering Casting: CustomAugmentations.HasSpellDurationAug (the
    /// guard) and CustomAugmentations.SpellDurationMultiplier (the term), which together are what all three
    /// duration sites now run - EnchantmentManager.Add's refresh path, EnchantmentManager.BuildEntry and
    /// AddEnchantmentResult.BuildStack.
    ///
    /// WHY THE GUARD IS TESTED HERE AND NOT AT THOSE SITES: none of them is reachable from this project.
    /// They take a live Player as caster and a Spell built from a real spell id, and no test in
    /// ACE.Server.Tests constructs a Player (WeaponModHooksATests's class remarks record the same
    /// limitation for Longevity, whose duration multiply sits two lines away at two of these sites). Pulling
    /// the guard out into a named pure predicate is what makes it testable at all, and
    /// HasSpellDurationAug_CustomOnly_IsTrue is the discriminating case: it FAILS against the un-edited
    /// "retail > 0" guard, which is the whole reason this feature needed those three sites touched.
    /// </summary>
    [TestClass]
    public class CustomAugSpellDurationTests
    {
        // Shipped default for custom_aug_spell_duration_bonus.
        private const double DefaultPerCustom = 0.10;

        /// <summary>
        /// THE REVERT-CHECK CASE. A player holding zero retail Archmage's Endurance augmentations and one
        /// Custom Dreamweave spell duration augmentation must get the term applied AND boosted. Against the
        /// pre-existing guard - AugmentationIncreasedSpellDuration > 0 alone - the term was skipped
        /// entirely, so this player got nothing at all rather than a smaller boost.
        /// </summary>
        [TestMethod]
        public void CustomOnlyHolder_GetsABoostedDuration()
        {
            Assert.IsTrue(CustomAugmentations.HasSpellDurationAug(0, 1),
                "A player holding only the custom augmentation must reach the duration term at all - this is the guard the old 'retail > 0' shape skipped.");

            var multiplier = CustomAugmentations.SpellDurationMultiplier(0, 1, DefaultPerCustom);

            Assert.IsTrue(multiplier > 1.0f, $"Expected a boost above 1.0, got {multiplier}.");
            Assert.AreEqual(1.10f, multiplier, 1e-6);
        }

        /// <summary>
        /// The guard is an OR over both counts, and is false only when the caster holds neither.
        /// </summary>
        [DataTestMethod]
        [DataRow(0, 0, false)]
        [DataRow(1, 0, true)]
        [DataRow(0, 1, true)]
        [DataRow(3, 2, true)]
        [DataRow(-1, -1, false)]
        public void HasSpellDurationAug_IsAnOrOverBothCounts(int retail, int custom, bool expected)
        {
            Assert.AreEqual(expected, CustomAugmentations.HasSpellDurationAug(retail, custom));
        }

        /// <summary>
        /// Retail and custom augmentations compose ADDITIVELY inside one term: 1 + retail*0.2 +
        /// custom*perCustom. Not a second multiplicative factor - WeaponModId.Longevity already multiplies
        /// one at the same sites.
        /// </summary>
        [DataTestMethod]
        [DataRow(0, 0, 1.0f)]
        [DataRow(1, 0, 1.2f)]
        [DataRow(5, 0, 2.0f)]
        [DataRow(0, 3, 1.3f)]
        [DataRow(5, 5, 2.5f)]   // 1 + 1.0 + 0.5, additive: a multiplicative pairing would give 3.0
        public void SpellDurationMultiplier_IsAdditive(int retail, int custom, float expected)
        {
            Assert.AreEqual(expected, CustomAugmentations.SpellDurationMultiplier(retail, custom, DefaultPerCustom), 1e-5f);
        }

        /// <summary>
        /// Nothing a corrupt property or a mis-set tunable can supply is allowed to SHORTEN a spell: the
        /// multiplier never drops below 1.0.
        /// </summary>
        [DataTestMethod]
        [DataRow(-5, -5, 0.10)]
        [DataRow(0, 5, -1.0)]
        [DataRow(0, 5, double.NaN)]
        [DataRow(0, 5, double.NegativeInfinity)]
        public void SpellDurationMultiplier_NeverShortensASpell(int retail, int custom, double perCustom)
        {
            Assert.AreEqual(1.0f, CustomAugmentations.SpellDurationMultiplier(retail, custom, perCustom), 1e-6f);
        }

        /// <summary>
        /// The retail step is unchanged at +20% per augmentation, so a player with no custom augmentations
        /// gets exactly what they got before this feature existed, whatever the custom tunable is set to.
        /// </summary>
        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(5)]
        public void SpellDurationMultiplier_RetailOnlyHolder_IsUnchanged(int retail)
        {
            var expected = 1.0f + retail * CustomAugmentations.RetailSpellDurationPerAug;

            Assert.AreEqual(expected, CustomAugmentations.SpellDurationMultiplier(retail, 0, DefaultPerCustom), 1e-6f);
            Assert.AreEqual(expected, CustomAugmentations.SpellDurationMultiplier(retail, 0, 5.0), 1e-6f);
        }

        /// <summary>
        /// Every catalog entry carries a wrong-item refusal, and every one of them still NAMES the currency.
        ///
        /// This is the discriminating half, not decoration. The line exists so Fi, Bo and Nacci refuse in
        /// their own voices, and the failure mode of writing in voice is a line that is pure character and
        /// no longer tells a confused player what to bring - "That does not appear in my ledger." on its own
        /// would read fine in review and be useless in game. Asserting the item name is what stops a future
        /// flavour pass from quietly deleting the instruction.
        ///
        /// The uniqueness check is the other half: three entries that all fell back to the same string would
        /// pass every other assertion here while defeating the entire point of the field.
        /// </summary>
        [TestMethod]
        public void Catalog_EveryEntry_HasAWrongItemRefusalNamingTheCurrency()
        {
            var seen = new System.Collections.Generic.HashSet<string>();

            foreach (var pair in CustomAugmentations.Catalog)
            {
                var refusal = pair.Value.WrongItemRefusal;

                Assert.IsFalse(string.IsNullOrWhiteSpace(refusal), $"{pair.Key} carries no wrong-item refusal");

                StringAssert.Contains(refusal, "Blank Augmentation Gem",
                    $"{pair.Key}'s refusal must name the currency - it is the only message telling a confused player what to bring");

                Assert.IsTrue(seen.Add(refusal), $"{pair.Key}'s refusal is a duplicate of another broker's - the per-broker voice is the reason this field exists");
            }
        }
    }
}
