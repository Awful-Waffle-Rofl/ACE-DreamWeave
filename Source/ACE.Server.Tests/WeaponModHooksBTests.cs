using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.WeaponMods;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The v4 hook phase for Mana Well, Cleanse and Siphon - the three catalog v4 rows wired in
    /// Player_WeaponMods.cs at the killing-blow site (Mana Well, Cleanse) and at the two damage sites
    /// (Siphon).
    ///
    /// WHAT IS COVERED HERE: every pure function the three hooks rest on. Mana Well's clamp arithmetic, the
    /// harmful-only selection filter Cleanse uses, the item-sourced exclusion BOTH Cleanse and Siphon inherit
    /// from the shipped dispel rule, and the roll threshold all three probability rows share.
    ///
    /// WHAT IS NOT, AND WHY. The hook bodies themselves need a live Player with a session, equipped objects
    /// and an enchantment registry, which ACE.Server.Tests cannot construct; the harmful test additionally
    /// needs DatManager, since Spell.IsHarmful is a dat read. That is precisely why the selection helper takes
    /// the harmful test as a PREDICATE rather than performing it inline - the rule is exercised below against a
    /// fake predicate, with no dat and no Player. What remains uncovered is that the live wrapper passes the
    /// real predicate and that the core sites call the hooks at all; those are live-loop checks.
    /// </summary>
    [TestClass]
    public class WeaponModHooksBTests
    {
        // ---------------- fixtures ----------------

        private static PropertiesEnchantmentRegistry Entry(int spellId, double duration = 30.0)
        {
            return new PropertiesEnchantmentRegistry
            {
                SpellId = spellId,
                Duration = duration,
                LayerId = 1,
            };
        }

        /// <summary>Odd spell ids are "harmful" in these tests. Nothing about the parity matters - it is a
        /// deterministic stand-in for the dat read the live predicate performs.</summary>
        private static bool OddIsHarmful(int spellId) => (spellId % 2) != 0;

        // ---------------- Mana Well clamp arithmetic ----------------

        [TestMethod]
        public void ManaWell_AddsWholePointsBelowTheCap()
        {
            // 3.0 points into an item 100 short of full: the whole magnitude lands
            Assert.AreEqual(3, WeaponModCombat.ManaWellTopUp(900, 1000, 3.0));
        }

        [TestMethod]
        public void ManaWell_ClampsToRemainingHeadroom()
        {
            // 5 points offered, 2 points of room: 2 land, and the item is never driven past its maximum
            Assert.AreEqual(2, WeaponModCombat.ManaWellTopUp(998, 1000, 5.0));
        }

        [TestMethod]
        public void ManaWell_FullItemTakesNothing()
        {
            Assert.AreEqual(0, WeaponModCombat.ManaWellTopUp(1000, 1000, 5.0));
        }

        [TestMethod]
        public void ManaWell_ItemAboveItsMaximumTakesNothing()
        {
            // negative headroom must not come back as a negative top-up
            Assert.AreEqual(0, WeaponModCombat.ManaWellTopUp(1200, 1000, 5.0));
        }

        [TestMethod]
        public void ManaWell_RoundsHalfAwayFromZero()
        {
            // matches RestoreFromMax's rounding, so a partial roll never truncates to nothing at the low end
            Assert.AreEqual(3, WeaponModCombat.ManaWellTopUp(0, 1000, 2.5));
            Assert.AreEqual(2, WeaponModCombat.ManaWellTopUp(0, 1000, 2.4));
            Assert.AreEqual(1, WeaponModCombat.ManaWellTopUp(0, 1000, 0.5));
        }

        [TestMethod]
        public void ManaWell_MagnitudeRoundingToZeroPaysNothing()
        {
            // below half a point there is no whole point to give, and 0 must not be reported as a top-up
            Assert.AreEqual(0, WeaponModCombat.ManaWellTopUp(0, 1000, 0.4));
        }

        [TestMethod]
        public void ManaWell_AbsentOrZeroMagnitudePaysNothing()
        {
            Assert.AreEqual(0, WeaponModCombat.ManaWellTopUp(0, 1000, 0.0));
            Assert.AreEqual(0, WeaponModCombat.ManaWellTopUp(0, 1000, -5.0));
            Assert.AreEqual(0, WeaponModCombat.ManaWellTopUp(0, 1000, double.NaN));
        }

        [TestMethod]
        public void ManaWell_ItemWithNoManaPoolTakesNothing()
        {
            Assert.AreEqual(0, WeaponModCombat.ManaWellTopUp(0, 0, 5.0));
            Assert.AreEqual(0, WeaponModCombat.ManaWellTopUp(0, -1, 5.0));
        }

        /// <summary>
        /// THE ROW IS FLAT POINTS, NOT A FRACTION. The single most expensive thing to get wrong about Mana Well
        /// is reading its magnitude as a fraction of the item's pool - a MaxRoll of 5 would then pay thousands
        /// on a high-capacity caster instead of five points. Pinned as a behavioural assertion: the payout is
        /// identical at wildly different item capacities.
        /// </summary>
        [TestMethod]
        public void ManaWell_PayoutDoesNotScaleWithItemCapacity()
        {
            Assert.AreEqual(5, WeaponModCombat.ManaWellTopUp(0, 100, 5.0));
            Assert.AreEqual(5, WeaponModCombat.ManaWellTopUp(0, 100000, 5.0));
        }

        // ---------------- Cleanse: harmful-only selection ----------------

        [TestMethod]
        public void SelectDispellableHarmful_KeepsOnlyHarmfulEntries()
        {
            var candidates = new List<PropertiesEnchantmentRegistry>
            {
                Entry(1),   // harmful
                Entry(2),   // beneficial
                Entry(3),   // harmful
                Entry(4),   // beneficial
            };

            var result = WeaponModCombat.SelectDispellableHarmful(candidates, OddIsHarmful);

            CollectionAssert.AreEquivalent(new[] { 1, 3 }, result.Select(e => e.SpellId).ToArray());
        }

        [TestMethod]
        public void SelectDispellableHarmful_AllBeneficialSelectsNothing()
        {
            var candidates = new List<PropertiesEnchantmentRegistry> { Entry(2), Entry(4) };

            Assert.AreEqual(0, WeaponModCombat.SelectDispellableHarmful(candidates, OddIsHarmful).Count);
        }

        [TestMethod]
        public void SelectDispellableHarmful_NullInputsAreEmptyNotNull()
        {
            Assert.AreEqual(0, WeaponModCombat.SelectDispellableHarmful(null, OddIsHarmful).Count);
            Assert.AreEqual(0, WeaponModCombat.SelectDispellableHarmful(new List<PropertiesEnchantmentRegistry> { Entry(1) }, null).Count);
        }

        // ---------------- every layer of the chosen spell (Cleanse AND Dispelling Edge) ----------------

        private static PropertiesEnchantmentRegistry Layer(int spellId, uint caster, SpellCategory category, double duration = 30.0)
        {
            var e = Entry(spellId, duration);
            e.CasterObjectId = caster;
            e.SpellCategory = category;
            return e;
        }

        /// <summary>
        /// The player report behind this rule: two archers each cast Gelidite's Gift, the registry held one
        /// layer per caster, and a strip of the top layer exposed the second at full strength. Every
        /// dispellable layer of the chosen category must go, including a lower-level spell in that category,
        /// while an item-sourced layer and an unrelated category are left alone.
        /// </summary>
        [TestMethod]
        public void SelectAllLayers_TakesEveryCastersCopyOfTheChosenCategory()
        {
            var chosen = Layer(2168, 0x80000001, SpellCategory.ColdVulnerability);

            var category = new List<PropertiesEnchantmentRegistry>
            {
                chosen,
                Layer(2168, 0x80000002, SpellCategory.ColdVulnerability),        // second archer, same spell
                Layer(1065, 0x80000003, SpellCategory.ColdVulnerability),        // lower level, same category
                Layer(2168, 0x80000004, SpellCategory.ColdVulnerability, -1.0),  // item-sourced: kept
                Layer(1109, 0x80000001, SpellCategory.FireVulnerability),        // stray other category: kept
            };

            var result = DispellingEdgeAbility.SelectAllLayers(chosen, category);

            CollectionAssert.AreEquivalent(new uint[] { 0x80000001, 0x80000002, 0x80000003 },
                result.Select(e => e.CasterObjectId).ToArray());
        }

        /// <summary>A strip never does less than the old one-layer strip: the chosen entry is always in.</summary>
        [TestMethod]
        public void SelectAllLayers_AlwaysIncludesTheChosenEntry()
        {
            var chosen = Layer(2168, 0x80000001, SpellCategory.ColdVulnerability);

            CollectionAssert.AreEqual(new[] { chosen }, DispellingEdgeAbility.SelectAllLayers(chosen, null));
            CollectionAssert.AreEqual(new[] { chosen }, DispellingEdgeAbility.SelectAllLayers(chosen, new List<PropertiesEnchantmentRegistry>()));
            Assert.AreEqual(0, DispellingEdgeAbility.SelectAllLayers(null, new List<PropertiesEnchantmentRegistry> { chosen }).Count);
        }

        // ---------------- the item-sourced exclusion (Cleanse AND Siphon) ----------------

        /// <summary>
        /// Duration == -1 marks an item-sourced enchantment (or vitae). Cleanse must not strip one off the
        /// wielder, and Siphon must never RESURRECT one on the player - a -1 duration survives onto a new
        /// registry entry untouched and would never expire. Both inherit the exclusion from the same shipped
        /// rule, so it is asserted through both entry points.
        /// </summary>
        [TestMethod]
        public void ItemSourced_IsExcludedFromCleanseSelection()
        {
            var candidates = new List<PropertiesEnchantmentRegistry>
            {
                Entry(1, -1.0),   // harmful, but item-sourced
                Entry(3, 30.0),   // harmful, a real cast
            };

            var result = WeaponModCombat.SelectDispellableHarmful(candidates, OddIsHarmful);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(3, result[0].SpellId);
        }

        [TestMethod]
        public void ItemSourced_IsExcludedFromSiphonSelection()
        {
            var candidates = new List<PropertiesEnchantmentRegistry>
            {
                Entry(2, -1.0),
                Entry(4, 30.0),
            };

            var result = DispellingEdgeAbility.SelectDispellable(candidates);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(4, result[0].SpellId);
        }

        /// <summary>
        /// The second shipped exclusion: a SpellId above short.MaxValue is an item/cooldown pseudo-spell rather
        /// than a real cast. Asserted here too, because Siphon would otherwise be able to steal a cooldown.
        /// </summary>
        [TestMethod]
        public void PseudoSpellIds_AreExcludedFromBothSelections()
        {
            var pseudo = short.MaxValue + 1;

            var candidates = new List<PropertiesEnchantmentRegistry> { Entry(pseudo, 30.0) };

            Assert.AreEqual(0, DispellingEdgeAbility.SelectDispellable(candidates).Count);
            Assert.AreEqual(0, WeaponModCombat.SelectDispellableHarmful(candidates, _ => true).Count);
        }

        // ---------------- the roll threshold ----------------

        /// <summary>
        /// Cleanse and Siphon both gate on RollsFree against a uniform draw in [0, 1). The comparison is
        /// STRICTLY LESS THAN, which is what makes a chance of exactly 0 unreachable and a draw of exactly the
        /// chance a miss rather than a hit - the difference between a 10% Siphon firing on 10% of hits and on
        /// slightly more.
        /// </summary>
        [TestMethod]
        public void Roll_FiresStrictlyBelowTheChance()
        {
            Assert.IsTrue(WeaponModCombat.RollsFree(0.10, 0.0999));
            Assert.IsFalse(WeaponModCombat.RollsFree(0.10, 0.10));
            Assert.IsFalse(WeaponModCombat.RollsFree(0.10, 0.1001));
        }

        [TestMethod]
        public void Roll_AbsentModifierNeverFires()
        {
            // an unequipped weapon, or the weapon_mods_enabled gate off, reads 0 - and 0 must never fire even
            // on the lowest possible draw
            Assert.IsFalse(WeaponModCombat.RollsFree(0.0, 0.0));
        }

        [TestMethod]
        public void Roll_CertainModifierAlwaysFires()
        {
            // Cleanse's MaxRoll is 1.00, so a perfect roll is a certainty and must fire on every draw
            Assert.IsTrue(WeaponModCombat.RollsFree(1.0, 0.0));
            Assert.IsTrue(WeaponModCombat.RollsFree(1.0, 0.9999999));
        }

        // ---------------- the rows themselves ----------------

        /// <summary>
        /// The three rows this phase wired, with the magnitude and shape each carries. Pinned here as well as
        /// in WeaponModCatalogV4Tests because the HOOKS read these numbers: Mana Well being flat rather than
        /// scaled is the whole reason ManaWellTopUp does no multiplication, and Cleanse/Siphon being
        /// probabilities is the reason they route through RollsFree instead.
        /// </summary>
        [TestMethod]
        public void Rows_CarryTheMagnitudesTheHooksAssume()
        {
            Assert.AreEqual(5.0, WeaponModRegistry.Get(WeaponModId.ManaWell).MaxRoll, 1e-9);
            Assert.AreEqual(1.0, WeaponModRegistry.Get(WeaponModId.ManaWell).DisplayScale, 1e-9);

            Assert.AreEqual(1.00, WeaponModRegistry.Get(WeaponModId.Cleanse).MaxRoll, 1e-9);
            Assert.AreEqual(0.10, WeaponModRegistry.Get(WeaponModId.Siphon).MaxRoll, 1e-9);
        }

        /// <summary>
        /// All three are TIER B, which is what keeps their magnitudes FRACTIONAL. A Tier B row writes no native
        /// property, so IsInteger is false and WeaponModValue.Resolve leaves a 0.037 alone instead of rounding
        /// it to 0 and then flooring it to 1 - which on Siphon would turn a 3.7% chance into a certainty.
        /// </summary>
        [TestMethod]
        public void Rows_AreTierBAndFractional()
        {
            foreach (var id in new[] { WeaponModId.ManaWell, WeaponModId.Cleanse, WeaponModId.Siphon })
            {
                var definition = WeaponModRegistry.Get(id);

                Assert.AreEqual(WeaponModTier.B, definition.Tier, $"{definition.DisplayName} must be Tier B");
                Assert.IsFalse(definition.WritesNative, $"{definition.DisplayName} must write no native property");
                Assert.IsFalse(definition.IsInteger, $"{definition.DisplayName} must stay fractional");
            }
        }

        /// <summary>
        /// All three are on every weapon class, which is what forces Siphon to wire BOTH a physical hook
        /// (reading the melee/missile weapon) and a spell hook (reading the wand), and forces Mana Well and
        /// Cleanse onto the killing-weapon accessor that resolves one carrier or the other.
        /// </summary>
        [TestMethod]
        public void Rows_AreAvailableToEveryWeaponClass()
        {
            foreach (var id in new[] { WeaponModId.ManaWell, WeaponModId.Cleanse, WeaponModId.Siphon })
            {
                var definition = WeaponModRegistry.Get(id);

                Assert.AreEqual(WeaponClass.All, definition.Classes, $"{definition.DisplayName} class set");
            }
        }
    }
}
