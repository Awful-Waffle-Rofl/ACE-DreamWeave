using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the TWELVE Blood Mage (7th class) entries to the design table in
    /// Docs/ClassAbilities/BLOOD-MAGE-DESIGN.md sec 3 (rev 6, 2026-08-03): which entries exist, their tier,
    /// max rank and per-rank CAP costs, and the CAP totals those costs must reproduce.
    ///
    /// REPRICED 2026-09-12 by the class ability overhaul's data pass. Four of the original eleven came off an
    /// escalating or flat-2 curve onto flat 1 per rank (Sanguine Reserve and Weakened Blood 1/2/3 -> 1/1/1,
    /// Malediction 2/2/2 -> 1/1/1) and Sanguine Ward came DOWN from 3/3/3 to 1/2/3. That pass took the totals
    /// from 15/18/15/48 to 12/12/12/36 and changed nothing about which entries exist.
    ///
    /// REPRICED AGAIN 2026-09-13 by the premium repricing, which took Malediction (T2) and Blood Price (T3)
    /// from 1 CAP per rank to 2. Malediction has therefore been 2/2/2, then 1/1/1, and is now 2/2/2 again -
    /// so a diff against an old revision of this file can look like the data pass was reverted. It was not:
    /// the 2026-09-12 flattening stands, and this is a separate, later decision to price the class's two
    /// strongest entries above the flat baseline. Totals went 12/12/12/36 -> 17/12/12/41 (Hemomancy) ->
    /// 17/15/15/47 (this reprice).
    ///
    /// THEN EXTENDED 2026-09-12 by the same overhaul's new-ability wave, which added a TWELFTH entry:
    /// Hemomancy, Blood Mage T1, 5 ranks at 1 CAP. That takes T1 from 12 to 17 and the class total from 36 to
    /// 41, the class's signed-off figure, and makes T1 a FIVE-entry tier. Its 5 points also restore the "a
    /// maxed T1 alone unlocks T3" property the data pass had broken - see
    /// <see cref="MaxedTier1_ExactlyMeetsTheTier3SpentInClassGate"/>, whose equality assertion it
    /// deliberately ended.
    ///
    /// The tier/cost half is the REGISTRATION layer only. Hemomancy was briefly this class's first pending
    /// entry since it shipped (Implemented = false, unlearnable, "[Coming soon]"), and its mechanic slice -
    /// the DoT tick bonus, the flat self-heal, and the Drain bonus - now lands in the same change that flips
    /// it to Implemented = true, so all twelve entries have a live mechanic again. The tests below still
    /// assert the exact set and the exact implemented/pending split, so a future entry cannot be registered
    /// as learnable before its mechanic lands. Enum ids alone never made an ability real - a rank only
    /// resolves once its definition is in ClassAbilityRegistry.byName.
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
            ("sanguine_reserve",   1, 3, new[] { 1, 1, 1 }),
            ("enhanced_lifemagic", 1, 3, new[] { 1, 1, 1 }),
            ("transfusion",        1, 3, new[] { 1, 1, 1 }),
            ("bloodmage_training", 1, 3, new[] { 1, 1, 1 }),
            // Added by the 2026-09-12 new-ability wave. FIVE ranks, not three like every entry above it -
            // it is the class's Tier 1 "splash" ability, and all eight classes got one.
            ("hemomancy",          1, 5, new[] { 1, 1, 1, 1, 1 }),
            // Tier 2 - 3 CAP earned, 5 spent in class
            ("weakened_blood",     2, 3, new[] { 1, 1, 1 }),
            // premium reprice 2026-09-13: 1/rank -> 2/rank
            ("malediction",        2, 3, new[] { 2, 2, 2 }),
            ("crimson_harvest",    2, 1, new[] { 3 }),
            ("heal_boost_rating",  2, 3, new[] { 1, 1, 1 }),
            // Tier 3 - 8 CAP earned, 15 spent in class
            ("exsanguinate",       3, 1, new[] { 3 }),
            // premium reprice 2026-09-13: 1/rank -> 2/rank
            ("blood_price",        3, 3, new[] { 2, 2, 2 }),
            ("sanguine_ward",      3, 3, new[] { 1, 2, 3 }),
        };

        // ---- registration ------------------------------------------------------------------------

        [TestMethod]
        public void TwelveEntries_AreRegisteredAndBelongToBloodMage()
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
        public void EntryShape_IsFiveFourThree()
        {
            var defs = ClassAbilityRegistry.Abilities.Values;

            // FIVE at T1 since the 2026-09-12 new-ability wave added Hemomancy; was four.
            Assert.AreEqual(5, ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.BloodMage, 1));
            Assert.AreEqual(4, ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.BloodMage, 2));
            Assert.AreEqual(3, ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.BloodMage, 3));
        }

        // ---- CAP arithmetic ----------------------------------------------------------------------

        [TestMethod]
        public void CapTotals_ReproduceTheDesignTable()
        {
            var defs = ClassAbilityRegistry.Abilities.Values;

            // Three moves, in order: the 2026-09-12 data-pass reprice took these from 15/18/15/48 to
            // 12/12/12/36; the same overhaul's new-ability wave added Hemomancy (T1, 5 ranks at 1 CAP),
            // taking T1 to 17 and the total to 41; then the 2026-09-13 PREMIUM REPRICING moved malediction
            // (T2) and blood_price (T3) from 1 CAP per rank to 2, adding 3 to each of those tiers and
            // taking the class to 47. T1 is untouched by that last step - neither repriced entry lives
            // there. These agree with ClassAbilityCapTotalsTests, which pins the same four numbers for all
            // eight classes.
            Assert.AreEqual(17, ClassAbilityCostSummary.MaxCostForClassTier(defs, ClassAbilityClass.BloodMage, 1), "T1 total");
            Assert.AreEqual(15, ClassAbilityCostSummary.MaxCostForClassTier(defs, ClassAbilityClass.BloodMage, 2), "T2 total");
            Assert.AreEqual(15, ClassAbilityCostSummary.MaxCostForClassTier(defs, ClassAbilityClass.BloodMage, 3), "T3 total");
            Assert.AreEqual(47, ClassAbilityCostSummary.MaxCostForClass(defs, ClassAbilityClass.BloodMage), "class total");
        }

        /// <summary>
        /// BLOOD-MAGE-DESIGN.md sec 3: "a fully maxed T1 [is] at exactly the T3 gate (15 spent in class), so
        /// T1 alone still unlocks T3". THE PROPERTY THAT MATTERS IS "T1 ALONE UNLOCKS T3", AND IT HOLDS. The
        /// EQUALITY that used to express it does not, and this assertion is >= rather than == for that
        /// reason.
        ///
        /// THE EQUALITY WAS AN ARTEFACT OF THE OLD 15-COST T1, NOT AN INVARIANT. It held only while Blood
        /// Mage T1 happened to cost exactly the 15 the gate asks for. The 2026-09-12 overhaul moved it twice:
        /// the reprice took T1 from 15 to 12, which left this test RED and a maxed T1 three points short of
        /// the gate; then the new-ability wave's Hemomancy (T1, 5 ranks at 1 CAP) took it to 17. So the gate
        /// is cleared again with 2 points to spare, and equality is permanently gone - ended deliberately by
        /// a signed-off addition, not drifted into. Restoring == would now mean repricing the class to hit a
        /// number the gate never required.
        ///
        /// WHAT IS LEFT HERE IS BLOOD-MAGE-SPECIFIC COVERAGE, NOT A UNIQUE INVARIANT.
        /// ClassAbilityCapTotalsTests.EveryClass_CanReachItsOwnTierGatesWhenMaxed already asserts this same
        /// >= property for every class including this one, against the live registry. This test survives
        /// because it is the one that fails FIRST and names Blood Mage when a Blood Mage reprice breaks the
        /// gate, where the all-class test names whichever class it reached first.
        /// </summary>
        [TestMethod]
        public void MaxedTier1_ExactlyMeetsTheTier3SpentInClassGate()
        {
            var maxedTier1 = ClassAbilityCostSummary.MaxCostForClassTier(
                ClassAbilityRegistry.Abilities.Values, ClassAbilityClass.BloodMage, 1);

            var gate = Tunable("class_ability_tier3_spent_required");

            Assert.IsTrue(maxedTier1 >= gate,
                $"a fully maxed Blood Mage T1 ({maxedTier1}) must reach the T3 spent-in-class gate ({gate})");
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
        /// ALL TWELVE Blood Mage entries do something today: Transfusion (the Drain surplus cascade), the
        /// three passive stat entries that ride shipped generated families, the five mechanics built
        /// 2026-08-03 - Sanguine Reserve, Weakened Blood, Crimson Harvest, Exsanguinate and Blood Price -
        /// Malediction (the Vulnerability/Imperil amplifier wired into EnchantmentManager), Sanguine Ward
        /// (the absorb pool, granted at the life-projectile cast site), and now Hemomancy (the DoT tick
        /// bonus / self-heal / Drain bonus mechanic slice, landed in the same change that flips this flag).
        ///
        /// HEMOMANCY WAS ABSENT FROM THIS LIST FROM THE 2026-09-12 new-ability wave UNTIL NOW. It was
        /// registered registration-only (Implemented = false, unlearnable), which is why the list below did
        /// not name it; its mechanic slice adds it here in the same change that gives it a live mechanic.
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
                    "exsanguinate", "heal_boost_rating", "hemomancy", "malediction", "sanguine_reserve",
                    "sanguine_ward", "transfusion", "weakened_blood",
                },
                implemented,
                "a Blood Mage entry was marked Implemented without a mechanic, or a live one was turned off");
        }

        /// <summary>
        /// THE PENDING SET IS EMPTY AGAIN. It briefly held exactly Hemomancy (registered registration-only
        /// by the 2026-09-12 new-ability wave, before its mechanic slice landed); this test asserted an EMPTY
        /// set from the 2026-08-03 merge until then, for the same reason it does again now - every Blood Mage
        /// entry currently registered has a live mechanic. The set is DERIVED from the registry rather than
        /// hardcoded, so a future registration-only entry fails this test loudly with its id, rather than
        /// silently going learnable.
        ///
        /// THE HOOK LOOP BELOW STAYS IN PLACE EVEN THOUGH IT RUNS ZERO TIMES RIGHT NOW - that cost nothing
        /// while the pending set was empty before, and it is what catches a registration-only entry added
        /// with a combat hook already wired (which would fire for a rank no player can buy) the next time
        /// this class registers one.
        /// </summary>
        [TestMethod]
        public void HemomancyIsTheOnlyPendingEntry_AndAPendingOneHooksNothing()
        {
            var pending = ClassAbilityRegistry.Abilities.Values
                .Where(d => d.AbilityClass == ClassAbilityClass.BloodMage && !d.Implemented)
                .Select(d => d.Id)
                .ToArray();

            CollectionAssert.AreEqual(
                new ClassAbilityId[0],
                pending,
                $"Hemomancy shipped its mechanic slice, so no Blood Mage entry should be pending anymore: {string.Join(", ", pending)}");

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
