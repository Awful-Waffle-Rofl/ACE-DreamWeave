using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the eleven Blood Mage (7th class) entries to the design table in
    /// Docs/ClassAbilities/BLOOD-MAGE-DESIGN.md sec 3 (rev 6, 2026-08-03): which entries exist, their tier,
    /// max rank and per-rank CAP costs, and the CAP totals those costs must reproduce (T1 15, T2 18, T3 15,
    /// total 48, with a maxed T1 exactly meeting the Tier 3 "spent in class" gate of 15).
    ///
    /// The tier/cost half is the REGISTRATION layer only. As of the 2026-08-03 merge all eleven have a live
    /// mechanic and are Implemented = true; the test still asserts the exact set, so a future entry cannot be
    /// registered as learnable before its mechanic lands. Enum ids alone never made an ability real -
    /// a rank only resolves once its definition is in ClassAbilityRegistry.byName.
    /// </summary>
    [TestClass]
    public class BloodMageAbilityDefinitionTests
    {
        private static ClassAbilityDefinition ByName(string name)
        {
            Assert.IsTrue(ClassAbilityRegistry.TryGetByName(name, out var def), $"{name} is not registered");
            return def;
        }

        private static long Tunable(string key) => PropertyManager.GetLong(key).Item;

        /// <summary>The design table: name, tier, max rank, per-rank cost.</summary>
        private static readonly (string Name, int Tier, int MaxRank, int[] Cost)[] DesignTable =
        {
            // Tier 1 - open
            ("sanguine_reserve",   1, 3, new[] { 1, 2, 3 }),
            ("enhanced_lifemagic", 1, 3, new[] { 1, 1, 1 }),
            ("transfusion",        1, 3, new[] { 1, 1, 1 }),
            ("bloodmage_training", 1, 3, new[] { 1, 1, 1 }),
            // Tier 2 - 3 CAP earned, 5 spent in class
            ("weakened_blood",     2, 3, new[] { 1, 2, 3 }),
            ("malediction",        2, 3, new[] { 2, 2, 2 }),
            ("crimson_harvest",    2, 1, new[] { 3 }),
            ("heal_boost_rating",  2, 3, new[] { 1, 1, 1 }),
            // Tier 3 - 8 CAP earned, 15 spent in class
            ("exsanguinate",       3, 1, new[] { 3 }),
            ("blood_price",        3, 3, new[] { 1, 1, 1 }),
            ("sanguine_ward",      3, 3, new[] { 3, 3, 3 }),
        };

        // ---- registration ------------------------------------------------------------------------

        [TestMethod]
        public void ElevenEntries_AreRegisteredAndBelongToBloodMage()
        {
            var registered = ClassAbilityRegistry.Abilities.Values
                .Where(d => d.AbilityClass == ClassAbilityClass.BloodMage)
                .Select(d => d.Name)
                .OrderBy(n => n)
                .ToArray();

            CollectionAssert.AreEqual(
                DesignTable.Select(e => e.Name).OrderBy(n => n).ToArray(),
                registered,
                "the set of Blood Mage entries drifted from the design table");
        }

        [TestMethod]
        public void EveryEntry_HasItsDesignTableTierRankAndCost()
        {
            foreach (var (name, tier, maxRank, cost) in DesignTable)
            {
                var def = ByName(name);

                Assert.AreEqual(ClassAbilityClass.BloodMage, def.AbilityClass, $"{name}: wrong class");
                Assert.AreEqual(tier, def.Tier, $"{name}: wrong tier");
                Assert.AreEqual(maxRank, def.MaxRank, $"{name}: wrong max rank");
                CollectionAssert.AreEqual(cost, def.CostPerRank, $"{name}: wrong per-rank cost");
            }
        }

        [TestMethod]
        public void EntryShape_IsFourFourThree()
        {
            var defs = ClassAbilityRegistry.Abilities.Values;

            Assert.AreEqual(4, ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.BloodMage, 1));
            Assert.AreEqual(4, ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.BloodMage, 2));
            Assert.AreEqual(3, ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.BloodMage, 3));
        }

        // ---- CAP arithmetic ----------------------------------------------------------------------

        [TestMethod]
        public void CapTotals_ReproduceTheDesignTable()
        {
            var defs = ClassAbilityRegistry.Abilities.Values;

            Assert.AreEqual(15, ClassAbilityCostSummary.MaxCostForClassTier(defs, ClassAbilityClass.BloodMage, 1), "T1 total");
            Assert.AreEqual(18, ClassAbilityCostSummary.MaxCostForClassTier(defs, ClassAbilityClass.BloodMage, 2), "T2 total");
            Assert.AreEqual(15, ClassAbilityCostSummary.MaxCostForClassTier(defs, ClassAbilityClass.BloodMage, 3), "T3 total");
            Assert.AreEqual(48, ClassAbilityCostSummary.MaxCostForClass(defs, ClassAbilityClass.BloodMage), "class total");
        }

        /// <summary>
        /// BLOOD-MAGE-DESIGN.md sec 3: "a fully maxed T1 [is] at exactly the T3 gate (15 spent in class), so
        /// T1 alone still unlocks T3". Equality is load-bearing - an intermediate 2-flat pricing draft was
        /// rejected precisely because it broke this by one point.
        /// </summary>
        [TestMethod]
        public void MaxedTier1_ExactlyMeetsTheTier3SpentInClassGate()
        {
            var maxedTier1 = ClassAbilityCostSummary.MaxCostForClassTier(
                ClassAbilityRegistry.Abilities.Values, ClassAbilityClass.BloodMage, 1);

            Assert.AreEqual(Tunable("class_ability_tier3_spent_required"), maxedTier1,
                "a fully maxed Blood Mage T1 must land exactly on the T3 spent-in-class gate");
        }

        [TestMethod]
        public void MaxedTier1_AlsoClearsTheTier2Gate()
        {
            var defs = ClassAbilityRegistry.Abilities.Values;

            // the cheapest single T1 entry a Blood Mage can max is 3 CAP, which is short of the T2 gate of 5,
            // so T2 genuinely costs a second purchase - the tier ladder is not free
            var cheapestMaxedT1Entry = defs
                .Where(d => d.AbilityClass == ClassAbilityClass.BloodMage && d.Tier == 1)
                .Min(d => d.CumulativeCost(d.MaxRank));

            Assert.IsTrue(cheapestMaxedT1Entry < Tunable("class_ability_tier2_spent_required"),
                "one maxed T1 entry should not be enough to reach T2 on its own");

            Assert.IsTrue(ClassAbilityCostSummary.MaxCostForClassTier(defs, ClassAbilityClass.BloodMage, 1)
                >= Tunable("class_ability_tier2_spent_required"));
        }

        // ---- tier gate is class-agnostic ---------------------------------------------------------

        /// <summary>
        /// The tier unlock (<see cref="ACE.Server.WorldObjects.Player.MeetsClassAbilityTierUnlock"/>) is keyed
        /// on the TIER NUMBER plus a caller-supplied spent-in-class total; there is no class parameter anywhere
        /// on that path, so it cannot carry a six-class list. It reads skill.Tier plus
        /// PointsSpentInClass(skill.AbilityClass), which sums CumulativeCost over every owned definition whose
        /// AbilityClass equals the one asked for. A 7th class therefore needs no gate change at all - only
        /// definitions carrying AbilityClass.BloodMage, which is what this branch adds. This test pins the
        /// tunables the gate reads (<see cref="ACE.Server.Managers.PropertyManager"/>) to the design table's
        /// thresholds rather than exercising a class-specific API, since there is none.
        /// </summary>
        [TestMethod]
        public void TierGate_TakesNoClassAndSoAppliesToASeventhClassUnchanged()
        {
            // T1 is unconditionally open (MeetsClassAbilityTierUnlock short-circuits on skill.Tier <= 1, no tunable involved)

            Assert.AreEqual(3, Tunable("class_ability_tier2_cap_required"));
            Assert.AreEqual(5, Tunable("class_ability_tier2_spent_required"));
            Assert.AreEqual(8, Tunable("class_ability_tier3_cap_required"));
            Assert.AreEqual(15, Tunable("class_ability_tier3_spent_required"));

            // every Blood Mage tier value in the design table is one the live gate already knows how to key on
            foreach (var tier in DesignTable.Select(e => e.Tier).Distinct())
                Assert.IsTrue(tier >= 1 && tier <= 3, $"tier {tier} is outside the gate's known range");
        }

        // ---- which mechanics are actually live ---------------------------------------------------

        /// <summary>
        /// All eleven Blood Mage entries do something today: Transfusion (the Drain surplus cascade), the
        /// three passive stat entries that ride shipped generated families, the five mechanics built
        /// 2026-08-03 - Sanguine Reserve, Weakened Blood, Crimson Harvest, Exsanguinate and Blood Price -
        /// Malediction (the Vulnerability/Imperil amplifier wired into EnchantmentManager) and Sanguine Ward
        /// (the absorb pool, granted at the life-projectile cast site). The list stays hardcoded even with
        /// nothing pending: it is what stops a twelfth entry being registered as learnable before its
        /// mechanic lands, letting a player spend CAP on an ability that changes nothing.
        /// </summary>
        [TestMethod]
        public void OnlyTheEntriesWithALiveMechanicAreImplemented()
        {
            var implemented = ClassAbilityRegistry.Abilities.Values
                .Where(d => d.AbilityClass == ClassAbilityClass.BloodMage && d.Implemented)
                .Select(d => d.Name)
                .OrderBy(n => n)
                .ToArray();

            CollectionAssert.AreEqual(
                new[]
                {
                    "blood_price", "bloodmage_training", "crimson_harvest", "enhanced_lifemagic",
                    "exsanguinate", "heal_boost_rating", "malediction", "sanguine_reserve", "sanguine_ward",
                    "transfusion", "weakened_blood",
                },
                implemented,
                "a Blood Mage entry was marked Implemented without a mechanic, or a live one was turned off");
        }

        /// <summary>
        /// Nothing is pending as of the 2026-08-03 merge - all eleven have a mechanic - so this asserts the
        /// pending set is EMPTY rather than listing ids that no longer exist. The set is DERIVED from the
        /// registry, and the hook loop is kept and driven off it: that costs nothing while the set is empty,
        /// and it is what catches a future registration-only entry added with a combat hook already wired,
        /// which would fire for a rank no player can buy.
        /// </summary>
        [TestMethod]
        public void NoEntryIsPending_AndAPendingOneWouldHookNothing()
        {
            var pending = ClassAbilityRegistry.Abilities.Values
                .Where(d => d.AbilityClass == ClassAbilityClass.BloodMage && !d.Implemented)
                .Select(d => d.Id)
                .ToArray();

            Assert.AreEqual(0, pending.Length,
                $"every Blood Mage entry has a live mechanic, so none may be Implemented = false: {string.Join(", ", pending)}");

            foreach (var id in pending)
            {
                var handler = ClassAbilityRegistry.GetHandler(id);

                Assert.IsFalse(handler.Definition.Implemented, $"{id}: mechanic is pending, so it must not be learnable");
                Assert.IsFalse(handler is IOutgoingDamageAbility, $"{id}: pending, must not hook combat");
                Assert.IsFalse(handler is IIncomingDamageAbility, $"{id}: pending, must not hook combat");
                Assert.IsFalse(handler is IMissileVolleyAbility, $"{id}: pending, must not hook combat");
                Assert.IsFalse(handler is IItemProcAbility, $"{id}: pending, must not hook combat");
                Assert.IsFalse(handler is ISpellHitAbility, $"{id}: pending, must not hook combat");
                Assert.IsFalse(handler is ICreatureDeathAbility, $"{id}: pending, must not hook combat");
            }
        }

        // ---- the three that ride generated families ----------------------------------------------

        [TestMethod]
        public void EnhancedLifeMagic_IsTheGeneratedEnhancedStatEntryHomedToBloodMageTier1()
        {
            var def = ByName("enhanced_lifemagic");

            // its registry id is the SYNTHETIC one derived from the skill, not ClassAbilityId.EnhancedLifeMagic
            // (52), which is a reserved-and-unused scaffold value
            Assert.AreEqual(EnhancedStatAbility.ClassIdForSkill(Skill.LifeMagic), def.Id);
            Assert.AreNotEqual(ClassAbilityId.EnhancedLifeMagic, def.Id);
            Assert.IsFalse(ClassAbilityRegistry.Abilities.ContainsKey(ClassAbilityId.EnhancedLifeMagic),
                "scaffold id 52 must stay unregistered - Enhanced Life Magic lives on its generated id");

            var handler = ClassAbilityRegistry.GetHandler(def.Id);
            Assert.IsInstanceOfType(handler, typeof(EnhancedStatAbility));
            Assert.AreEqual(Skill.LifeMagic, ((EnhancedStatAbility)handler).TargetSkill);

            // +10/25/50 base Life Magic at 1/1/1, the family's shape
            Assert.AreEqual(10, EnhancedStatAbility.BonusForRank(1));
            Assert.AreEqual(25, EnhancedStatAbility.BonusForRank(2));
            Assert.AreEqual(50, EnhancedStatAbility.BonusForRank(3));
        }

        [TestMethod]
        public void BloodMageTraining_IsABundleOverHealingCreatureEnchantmentAndCooking()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.BloodMageTraining);

            Assert.IsInstanceOfType(handler, typeof(BundleStatAbility));

            CollectionAssert.AreEqual(
                new[] { Skill.Healing, Skill.CreatureEnchantment, Skill.Cooking },
                ((BundleStatAbility)handler).BundledSkills.ToArray());
        }

        [TestMethod]
        public void HealBoostRating_IsARatingEntryWithTheFamilyBonusCurve()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.HealBoostRating);

            Assert.IsInstanceOfType(handler, typeof(RatingAbility));

            // +3/6/10, matching the design table's "+3/6/10 Heal Boost Rating"
            Assert.AreEqual(3, RatingAbility.BonusForRank(1));
            Assert.AreEqual(6, RatingAbility.BonusForRank(2));
            Assert.AreEqual(10, RatingAbility.BonusForRank(3));
            Assert.AreEqual(0, RatingAbility.BonusForRank(0));
        }
    }
}
