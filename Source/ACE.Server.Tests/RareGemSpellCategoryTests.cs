using System;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards the rare-category predicate the Proving Grounds buff strip is built on
    /// (ACE.Server.Entity.RareGemSpells.IsRareSpellCategory).
    /// <para/>
    /// The rule the strip implements is "every Prodigal spell goes; every Incantation and every Aura of
    /// Incantation survives". A dat probe of all 138 rare-gem candidate spells showed that rule is exactly
    /// "the spell's SpellCategory is a rare one" - all 67 Prodigal spells have a rare category and none of the
    /// other 71 do. So the predicate below is the whole feature: get it wrong in the permissive direction and
    /// players lose their own self-buffs on arrival, get it wrong in the strict direction and a rare gem's
    /// buff rides into the arena.
    /// <para/>
    /// Pure enum reflection - no Player, no world, no database. (Constructing a Player in this test host fails
    /// in its static initializer, so these tests deliberately never touch one.)
    /// </summary>
    [TestClass]
    public class RareGemSpellCategoryTests
    {
        /// <summary>
        /// The "*Rare"-SUFFIXED shape, which is most of them.
        /// </summary>
        [TestMethod]
        public void RareSuffixedCategories_AreRare()
        {
            Assert.AreEqual(449, (int)SpellCategory.AcidResistanceRaisingRare);
            Assert.AreEqual(458, (int)SpellCategory.ArmorValueRaisingRare);
            Assert.AreEqual(473, (int)SpellCategory.DamageRaisingRare);
            Assert.AreEqual(448, (int)SpellCategory.AcidProtectionRare);
            Assert.AreEqual(648, (int)SpellCategory.VoidMagicRaisingRare);

            Assert.IsTrue(RareGemSpells.IsRareSpellCategory(SpellCategory.AcidResistanceRaisingRare));
            Assert.IsTrue(RareGemSpells.IsRareSpellCategory(SpellCategory.ArmorValueRaisingRare));
            Assert.IsTrue(RareGemSpells.IsRareSpellCategory(SpellCategory.DamageRaisingRare));
            Assert.IsTrue(RareGemSpells.IsRareSpellCategory(SpellCategory.AcidProtectionRare));
            Assert.IsTrue(RareGemSpells.IsRareSpellCategory(SpellCategory.VoidMagicRaisingRare));
        }

        /// <summary>
        /// The "Rare*"-PREFIXED shape. These are the ones a naive EndsWith("Rare") test would miss, which is
        /// why the predicate handles both shapes.
        /// </summary>
        [TestMethod]
        public void RarePrefixedCategories_AreRare()
        {
            Assert.AreEqual(679, (int)SpellCategory.RareDirtyFightingRaising);
            Assert.AreEqual(680, (int)SpellCategory.RareDualWieldRaising);
            Assert.AreEqual(681, (int)SpellCategory.RareRecklessnessRaising);
            Assert.AreEqual(682, (int)SpellCategory.RareShieldRaising);
            Assert.AreEqual(683, (int)SpellCategory.RareSneakAttackRaising);
            Assert.AreEqual(634, (int)SpellCategory.RareDamageReductionRatingRaising);
            Assert.AreEqual(694, (int)SpellCategory.RareDamageRatingRaising2);

            Assert.IsTrue(RareGemSpells.IsRareSpellCategory(SpellCategory.RareDirtyFightingRaising));
            Assert.IsTrue(RareGemSpells.IsRareSpellCategory(SpellCategory.RareDualWieldRaising));
            Assert.IsTrue(RareGemSpells.IsRareSpellCategory(SpellCategory.RareRecklessnessRaising));
            Assert.IsTrue(RareGemSpells.IsRareSpellCategory(SpellCategory.RareShieldRaising));
            Assert.IsTrue(RareGemSpells.IsRareSpellCategory(SpellCategory.RareSneakAttackRaising));
            Assert.IsTrue(RareGemSpells.IsRareSpellCategory(SpellCategory.RareDamageReductionRatingRaising));
            Assert.IsTrue(RareGemSpells.IsRareSpellCategory(SpellCategory.RareDamageRatingRaising2));
        }

        /// <summary>
        /// The plain categories carried by the Incantation / Aura line. These are the buffs a player casts on
        /// themselves and the whole reason the id-only strip had to be narrowed - every one of these must
        /// survive walking through a Proving Grounds portal.
        /// </summary>
        [TestMethod]
        public void PlainCategories_AreNotRare()
        {
            Assert.AreEqual(37, (int)SpellCategory.MeleeDefenseRaising);
            Assert.AreEqual(41, (int)SpellCategory.MagicDefenseRaising);
            Assert.AreEqual(115, (int)SpellCategory.ArmorRaising);
            Assert.AreEqual(152, (int)SpellCategory.AttackModRaising);
            Assert.AreEqual(154, (int)SpellCategory.DamageRaising);
            Assert.AreEqual(156, (int)SpellCategory.DefenseModRaising);
            Assert.AreEqual(158, (int)SpellCategory.WeaponTimeRaising);
            Assert.AreEqual(160, (int)SpellCategory.ArmorValueRaising);
            Assert.AreEqual(162, (int)SpellCategory.AcidResistanceRaising);
            Assert.AreEqual(195, (int)SpellCategory.ManaConversionModRaising);
            Assert.AreEqual(695, (int)SpellCategory.SpellDamageRaising);

            Assert.IsFalse(RareGemSpells.IsRareSpellCategory(SpellCategory.MeleeDefenseRaising));
            Assert.IsFalse(RareGemSpells.IsRareSpellCategory(SpellCategory.MagicDefenseRaising));
            Assert.IsFalse(RareGemSpells.IsRareSpellCategory(SpellCategory.ArmorRaising));
            Assert.IsFalse(RareGemSpells.IsRareSpellCategory(SpellCategory.AttackModRaising));
            Assert.IsFalse(RareGemSpells.IsRareSpellCategory(SpellCategory.DamageRaising));
            Assert.IsFalse(RareGemSpells.IsRareSpellCategory(SpellCategory.DefenseModRaising));
            Assert.IsFalse(RareGemSpells.IsRareSpellCategory(SpellCategory.WeaponTimeRaising));
            Assert.IsFalse(RareGemSpells.IsRareSpellCategory(SpellCategory.ArmorValueRaising));
            Assert.IsFalse(RareGemSpells.IsRareSpellCategory(SpellCategory.AcidResistanceRaising));
            Assert.IsFalse(RareGemSpells.IsRareSpellCategory(SpellCategory.ManaConversionModRaising));
            Assert.IsFalse(RareGemSpells.IsRareSpellCategory(SpellCategory.SpellDamageRaising));
        }

        /// <summary>
        /// ExtraRecklessnessRaising (672) is NOT a rare category, but its name contains "rare" as a
        /// case-insensitive substring ("ext-RA-RE-cklessness"). It is the single member in the whole enum with
        /// that property, and it is why the predicate's comparisons are Ordinal rather than OrdinalIgnoreCase,
        /// and are anchored StartsWith/EndsWith rather than Contains.
        /// </summary>
        [TestMethod]
        public void ExtraRecklessnessRaising_IsNotRare_DespiteContainingTheSubstring()
        {
            Assert.AreEqual(672, (int)SpellCategory.ExtraRecklessnessRaising);

            Assert.IsTrue(nameof(SpellCategory.ExtraRecklessnessRaising)
                .IndexOf("rare", StringComparison.OrdinalIgnoreCase) >= 0,
                "the trap this test guards has moved - the name no longer contains a case-insensitive 'rare'");

            Assert.IsFalse(RareGemSpells.IsRareSpellCategory(SpellCategory.ExtraRecklessnessRaising));
        }

        /// <summary>
        /// Whole-enum sweep: the predicate must agree with the enum's own naming for every single member, and
        /// the rare population must be the 76 members that exist today. The count is pinned deliberately - if a
        /// future dat sync adds rare categories, this failing is the signal to re-probe the candidate set rather
        /// than to bump the number blindly.
        /// </summary>
        [TestMethod]
        public void Predicate_AgreesWithEnumNaming_AcrossEveryMember()
        {
            var names = Enum.GetNames(typeof(SpellCategory));
            var rareCount = 0;

            foreach (var name in names)
            {
                var category = (SpellCategory)Enum.Parse(typeof(SpellCategory), name);
                var expected = name.StartsWith("Rare", StringComparison.Ordinal)
                    || name.EndsWith("Rare", StringComparison.Ordinal);

                Assert.AreEqual(expected, RareGemSpells.IsRareSpellCategory(category),
                    $"predicate disagreed with the naming of SpellCategory.{name}");

                if (expected)
                    rareCount++;
            }

            Assert.AreEqual(76, rareCount, "the rare-category population of SpellCategory changed");
        }

        /// <summary>
        /// A value with no enum member is not a rare category - Enum.GetName returns null there and the
        /// predicate must not throw or guess.
        /// </summary>
        [TestMethod]
        public void UndefinedCategoryValue_IsNotRare()
        {
            // SpellCategory's underlying type is uint, so stay in uint here - Enum.IsDefined throws on a value
            // whose type is not the enum's underlying type.
            uint undefined = Enum.GetValues(typeof(SpellCategory)).Cast<SpellCategory>().Max(c => (uint)c) + 1000;

            Assert.IsFalse(Enum.IsDefined(typeof(SpellCategory), undefined));
            Assert.IsFalse(RareGemSpells.IsRareSpellCategory((SpellCategory)undefined));
        }
    }
}
