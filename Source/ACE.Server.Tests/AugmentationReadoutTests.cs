using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Structural coverage for the /xpaugs and /lumaugs catalogs in AugmentationCommands.
    ///
    /// WHY THIS EXISTS: both readouts are hand-maintained tables that mirror data owned elsewhere -
    /// AugmentationDevice.MaxAugs/AugProps for the experience augmentations, and the LumAug* block of
    /// PropertyInt for the luminance auras. Drift between a table and its source fails SILENTLY and in
    /// the worst possible direction: a newly added augmentation simply does not appear in the player's
    /// list, so the command keeps working, keeps printing a plausible list, and quietly under-reports.
    /// Nothing at runtime notices, because nothing at runtime enumerates the source.
    ///
    /// These tests do that enumeration. They assert coverage and mutual exclusivity only - the display
    /// names and effect blurbs are prose and are deliberately not pinned here.
    /// </summary>
    [TestClass]
    public class AugmentationReadoutTests
    {
        /// <summary>
        /// The LumAug* properties that exist in PropertyInt but that no NPC in the world database can
        /// grant, so no character can ever hold one and /lumaugs must not offer them. Verified against
        /// ace_world by looking for any emote action targeting each stat: LumAugSurgeEffectRating (337),
        /// LumAugVitality (341) and LumAugNoDestroyCraft (345) return no rows, and LumAugVitality has no
        /// reader anywhere in ACE.Server either.
        ///
        /// If a future content drop starts granting one of these, delete it from this set and add the
        /// aura to AugmentationCommands.BaseLumAugs - that is exactly the drift this test exists to catch.
        /// </summary>
        private static readonly HashSet<PropertyInt> UngrantableLumAugProperties = new HashSet<PropertyInt>
        {
            PropertyInt.LumAugSurgeEffectRating,
            PropertyInt.LumAugVitality,
            PropertyInt.LumAugNoDestroyCraft,
        };

        /// <summary>
        /// Every AugmentationType any readout in AugmentationCommands prints - the three XP tables PLUS the
        /// Custom Dreamweave table, which is printed by /customaugs rather than /xpaugs.
        ///
        /// The Custom Dreamweave rows are included here because the coverage assertion below enumerates
        /// AugmentationDevice.MaxAugs, which is the ONE source of truth for "an augmentation a player can
        /// buy" and now holds the three custom types too. A type that appears in MaxAugs but in no readout
        /// is invisible to players, whichever command would have shown it - that is the drift this guards,
        /// and narrowing the assertion to the XP tables instead would have silently retired it.
        ///
        /// CustomDreamweaveAugs also carries one row with a NULL Type - the shipped Quickhand quest boon,
        /// which is not an augmentation and has no MaxAugs entry. It is skipped here rather than being
        /// excluded from the table, because it genuinely belongs in the readout.
        /// </summary>
        private static IEnumerable<AugmentationType> XpCatalogTypes()
        {
            foreach (var entry in AugmentationCommands.InnateAttributes)
                yield return entry.Type;

            foreach (var entry in AugmentationCommands.InnateResistances)
                yield return entry.Type;

            foreach (var entry in AugmentationCommands.SingleXpAugs)
                yield return entry.Type;

            foreach (var entry in AugmentationCommands.CustomDreamweaveAugs)
            {
                if (entry.Type.HasValue)
                    yield return entry.Type.Value;
            }
        }

        [TestMethod]
        public void XpCatalog_CoversEveryPurchasableAugmentation()
        {
            var listed = XpCatalogTypes().ToList();
            var missing = AugmentationDevice.MaxAugs.Keys.Except(listed).ToList();

            Assert.AreEqual(0, missing.Count,
                $"/xpaugs does not list: {string.Join(", ", missing)}. Every AugmentationType a player can buy must appear in InnateAttributes, InnateResistances, SingleXpAugs or CustomDreamweaveAugs.");
        }

        /// <summary>
        /// The Custom Dreamweave augmentations are UNCAPPED by design - the price curve is the only limit,
        /// and /customaugs prints a flat list with no denominator on exactly that basis. A future edit that
        /// gave one of them a finite cap would silently make the readout lie about it, and would put a wall
        /// in front of a purchase the broker would still happily sell.
        /// </summary>
        [TestMethod]
        public void CustomDreamweaveAugs_AreUncapped()
        {
            foreach (var entry in AugmentationCommands.CustomDreamweaveAugs)
            {
                if (!entry.Type.HasValue)
                    continue;

                var type = entry.Type.Value;

                Assert.IsTrue(AugmentationDevice.MaxAugs.ContainsKey(type),
                    $"{entry.Name} names {type}, which has no AugmentationDevice.MaxAugs entry.");

                Assert.AreEqual(int.MaxValue, AugmentationDevice.MaxAugs[type],
                    $"{entry.Name} ({type}) must stay uncapped: /customaugs prints it with no denominator and CustomAugBroker keeps selling it.");
            }
        }

        /// <summary>
        /// Exactly one row - the shipped Quickhand quest boon - is allowed to have no AugmentationType. Any
        /// other null Type would be an augmentation that silently escapes the uncapped assertion above.
        /// </summary>
        [TestMethod]
        public void CustomDreamweaveAugs_HaveExactlyOneNonAugmentationRow()
        {
            var untyped = AugmentationCommands.CustomDreamweaveAugs.Where(entry => !entry.Type.HasValue).ToList();

            Assert.AreEqual(1, untyped.Count,
                $"Expected exactly one non-augmentation row (the Quickhand quest boon), found: {string.Join(", ", untyped.Select(entry => entry.Name))}.");
        }

        [TestMethod]
        public void XpCatalog_ListsNothingThatCannotBePurchased()
        {
            var unknown = XpCatalogTypes().Where(type => !AugmentationDevice.MaxAugs.ContainsKey(type) || !AugmentationDevice.AugProps.ContainsKey(type)).ToList();

            Assert.AreEqual(0, unknown.Count,
                $"/xpaugs lists augmentations with no cap or no backing property: {string.Join(", ", unknown)}.");
        }

        [TestMethod]
        public void XpCatalog_ListsEachAugmentationExactlyOnce()
        {
            var duplicates = XpCatalogTypes().GroupBy(type => type).Where(group => group.Count() > 1).Select(group => group.Key).ToList();

            Assert.AreEqual(0, duplicates.Count,
                $"/xpaugs would print these augmentations more than once: {string.Join(", ", duplicates)}.");
        }

        [TestMethod]
        public void LumCatalog_CoversEveryGrantableLumAugProperty()
        {
            var listed = AugmentationCommands.BaseLumAugs.Select(aura => aura.Prop)
                .Concat(AugmentationCommands.Seers.SelectMany(seer => seer.Auras).Select(aura => aura.Prop))
                .ToHashSet();

            var missing = Enum.GetValues(typeof(PropertyInt)).Cast<PropertyInt>()
                .Where(prop => prop.ToString().StartsWith("LumAug", StringComparison.Ordinal))
                .Where(prop => !UngrantableLumAugProperties.Contains(prop))
                .Where(prop => !listed.Contains(prop))
                .ToList();

            Assert.AreEqual(0, missing.Count,
                $"/lumaugs does not list: {string.Join(", ", missing)}. Add the aura to BaseLumAugs or to a Seer, or add the property to UngrantableLumAugProperties with the evidence that nothing grants it.");
        }

        [TestMethod]
        public void LumCatalog_DoesNotListAnUngrantableAura()
        {
            var listed = AugmentationCommands.BaseLumAugs.Select(aura => aura.Prop)
                .Concat(AugmentationCommands.Seers.SelectMany(seer => seer.Auras).Select(aura => aura.Prop))
                .ToList();

            var offered = listed.Where(UngrantableLumAugProperties.Contains).Distinct().ToList();

            Assert.AreEqual(0, offered.Count,
                $"/lumaugs offers auras no NPC can grant: {string.Join(", ", offered)}.");
        }

        [TestMethod]
        public void EverySeer_OffersTwoAurasAndOwnsAUniqueLoyaltyQuest()
        {
            Assert.AreEqual(4, AugmentationCommands.Seers.Length, "There are four Seers.");

            foreach (var seer in AugmentationCommands.Seers)
            {
                Assert.AreEqual(2, seer.Auras.Length, $"{seer.DisplayName} must offer exactly two auras.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(seer.QuestName), $"{seer.DisplayName} has no loyalty quest.");
            }

            var quests = AugmentationCommands.Seers.Select(seer => seer.QuestName).ToList();

            CollectionAssert.AllItemsAreUnique(quests, "Two Seers share a loyalty quest, so BuildSeerRow could attribute one Seer's auras to another.");
        }

        /// <summary>
        /// The SharedWithBaseAura flag is what decides whether BuildSeerRow subtracts the base aura's
        /// five ranks off the property before displaying it, so a wrong flag silently prints the wrong
        /// number. It is true exactly when the same property also appears in BaseLumAugs.
        /// </summary>
        [TestMethod]
        public void SeerAuras_ShareABasePropertyExactlyWhenFlagged()
        {
            var baseProps = AugmentationCommands.BaseLumAugs.ToDictionary(aura => aura.Prop, aura => aura.Max);

            foreach (var seer in AugmentationCommands.Seers)
            {
                foreach (var (name, prop, sharedWithBaseAura) in seer.Auras)
                {
                    var isBaseAura = baseProps.ContainsKey(prop);

                    Assert.AreEqual(isBaseAura, sharedWithBaseAura,
                        $"{seer.DisplayName}'s {name} is marked SharedWithBaseAura={sharedWithBaseAura} but {prop} {(isBaseAura ? "is" : "is not")} a base aura property.");

                    if (sharedWithBaseAura)
                    {
                        Assert.AreEqual(AugmentationCommands.LumAuraMaxRank, baseProps[prop],
                            $"{name} continues {prop} above the base aura's ranks, so the base aura must cap at {AugmentationCommands.LumAuraMaxRank}.");
                    }
                }
            }
        }
    }
}
