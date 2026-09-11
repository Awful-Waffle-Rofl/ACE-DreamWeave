using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Target-aware monster spell selection - the pure decision behind "vuln the player if they do not
    /// already have that vuln, avoid re-vulning, cast damage once the vulns are in place".
    ///
    /// The load-bearing thing pinned here is NOT which spell wins. It is that the change cannot alter how
    /// often a monster casts. The pre-existing roll is one independent Bernoulli trial per book entry, so
    /// P(cast nothing) = prod(1 - p_i) - a product, and therefore order-independent. Reordering the book
    /// is free; DROPPING an entry is not, because it lowers P(cast at all) and would quietly turn every
    /// vuln-heavy caster in the game into a melee mob. So the neutrality tests below assert that all three
    /// passes together roll every book entry EXACTLY ONCE, on a full book, on a Threads post-strip book,
    /// and on a book where every single entry is Redundant.
    ///
    /// The probe order is recovered through the public API alone: TrySelect is run once per k with an rng
    /// rigged to succeed on its k-th call, so the spell it returns IS the k-th entry it rolled. Nothing
    /// here needs a live Creature, Player, spell table or database.
    ///
    /// Every spell constant is spelled out as the local ace_world stores it, verified by query at authoring
    /// time rather than recalled.
    /// </summary>
    [TestClass]
    public class MonsterSpellSelectionTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ------------------------------------------------------------------
        // ace_world shapes, spelled out
        // ------------------------------------------------------------------

        // Fire Vulnerability Other VI (1108) and Fire Protection Other VI (1096) are BOTH stat_Mod_Type
        // 20488 on stat_Mod_Key 67. Only stat_Mod_Val tells them apart: 2.5 against 0.4.
        private const EnchantmentTypeFlags VulnType =
            EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative;

        // Incantation of Imperil Other (4312): stat_Mod_Type 41088, stat_Mod_Key NULL (0 in C#),
        // stat_Mod_Val -225. Armor Self is the same type on the other side of the 0.0 identity.
        private const EnchantmentTypeFlags ImperilType =
            EnchantmentTypeFlags.BodyArmorValue | EnchantmentTypeFlags.MultipleStat | EnchantmentTypeFlags.Additive;

        private const uint ResistFireKey = (uint)PropertyFloat.ResistFire;      // 67
        private const uint ResistColdKey = (uint)PropertyFloat.ResistCold;      // 68

        // spell ids used in the fixtures
        private const int FireVulnOtherVI = 1108;       // fire vuln, stat_Mod_Val 2.5
        private const int GeliditesGift = 2168;         // COLD vuln, stat_Mod_Val 2.85 - see below
        private const int ImperilOther = 4312;          // body-armor debuff, stat_Mod_Val -225
        private const int FlameBolt = 4439;             // fire damage, e_Type 16, 1 projectile
        private const int HarmOtherVI = 1176;           // no stat mod at all, e_Type NULL -> Undef
        private const int ExsanguinatingWave = 3940;    // banned by id; e_Type 128 (Health), 1 projectile
        private const int ExplodingMagma = 1781;        // stripped by the projectile rule; e_Type 16, 9 projectiles

        private static MonsterSpellShape ShapeOf(int spellId)
        {
            switch (spellId)
            {
                case FireVulnOtherVI:
                    return MonsterSpellShape.FromSpellFields(VulnType, ResistFireKey, 2.5f, DamageType.Undef);
                case GeliditesGift:
                    return MonsterSpellShape.FromSpellFields(VulnType, ResistColdKey, 2.85f, DamageType.Undef);
                case ImperilOther:
                    return MonsterSpellShape.FromSpellFields(ImperilType, 0, -225.0f, DamageType.Undef);
                case FlameBolt:
                    return MonsterSpellShape.FromSpellFields(0, 0, 204.0f, DamageType.Fire);
                case HarmOtherVI:
                    return MonsterSpellShape.FromSpellFields(0, 0, 0.0f, DamageType.Undef);
                case ExsanguinatingWave:
                    return MonsterSpellShape.FromSpellFields(0, 0, 0.0f, DamageType.Health);
                case ExplodingMagma:
                    return MonsterSpellShape.FromSpellFields(0, 0, 0.0f, DamageType.Fire);
                default:
                    return default;
            }
        }

        private static readonly Func<int, MonsterSpellShape> Shapes = ShapeOf;

        // ------------------------------------------------------------------
        // target snapshots
        // ------------------------------------------------------------------

        private static VulnerabilityProfile Clean => VulnerabilityProfile.None;

        private static VulnerabilityProfile WithFireVuln(float mod) =>
            new VulnerabilityProfile(1.0f, 1.0f, 1.0f, 1.0f, mod, 1.0f, 1.0f, 0);

        private static VulnerabilityProfile FullyDebuffed =>
            new VulnerabilityProfile(1.0f, 1.0f, 1.0f, 2.85f, 2.5f, 1.0f, 1.0f, -225);

        // ------------------------------------------------------------------
        // the flag constants themselves
        // ------------------------------------------------------------------

        [TestMethod]
        public void EnchantmentTypeFlagCombinations_MatchTheStoredStatModTypes()
        {
            // if these drift, every shape assertion below is testing a different spell than it claims to
            Assert.AreEqual(20488, (int)VulnType);
            Assert.AreEqual(41088, (int)ImperilType);
            Assert.AreEqual(67u, ResistFireKey);
            Assert.AreEqual(68u, ResistColdKey);
        }

        [TestMethod]
        public void Tunable_IsRegisteredAndDefaultsToTheConst()
        {
            // the registered value, read back out of the tunable cache - not a compile-time constant
            Assert.IsTrue(PropertyManager.GetBool("monster_conditional_spell_selection").Item,
                "monster_conditional_spell_selection must be registered and must default to on");

            Assert.AreEqual(MonsterSpellSelector.DefaultConditionalSelectionEnabled,
                PropertyManager.GetBool("monster_conditional_spell_selection").Item,
                "the registration must reference the const, not a literal");
        }

        // ------------------------------------------------------------------
        // FromSpellFields: the shape predicate
        // ------------------------------------------------------------------

        [TestMethod]
        public void FromSpellFields_FireVulnerability_IsAFireVulnerability()
        {
            var shape = MonsterSpellShape.FromSpellFields(VulnType, ResistFireKey, 2.5f, DamageType.Undef);

            Assert.IsTrue(shape.IsVulnerability);
            Assert.AreEqual(DamageType.Fire, shape.VulnElement);
            Assert.AreEqual(2.5f, shape.VulnStatModVal, 1e-6f);
            Assert.IsFalse(shape.IsBodyArmorDebuff);
        }

        [TestMethod]
        public void FromSpellFields_FireProtection_IsNotAVulnerability()
        {
            // identical stat_Mod_Type and stat_Mod_Key to Fire Vulnerability; only the value differs
            var shape = MonsterSpellShape.FromSpellFields(VulnType, ResistFireKey, 0.4f, DamageType.Undef);

            Assert.IsFalse(shape.IsVulnerability);
            Assert.AreEqual(DamageType.Undef, shape.VulnElement);
            Assert.IsFalse(shape.IsBodyArmorDebuff);
        }

        [TestMethod]
        public void FromSpellFields_GeliditesGift_IsAColdVulnerabilityDespiteItsName()
        {
            // Gelidite's Gift (spell 2168) reads like a damage spell or a gift buff and is neither:
            // stat_Mod_Key 68 (ResistCold), stat_Mod_Val 2.85. This is the case a name-based or
            // id-list-based predicate gets wrong and the shape predicate gets right.
            var shape = ShapeOf(GeliditesGift);

            Assert.IsTrue(shape.IsVulnerability);
            Assert.AreEqual(DamageType.Cold, shape.VulnElement);
            Assert.AreEqual(2.85f, shape.VulnStatModVal, 1e-6f);
            Assert.AreEqual(DamageType.Undef, shape.ElementalDamageType);
        }

        [TestMethod]
        public void FromSpellFields_Imperil_IsABodyArmorDebuff()
        {
            var shape = ShapeOf(ImperilOther);

            Assert.IsTrue(shape.IsBodyArmorDebuff);
            Assert.IsFalse(shape.IsVulnerability);
        }

        [TestMethod]
        public void FromSpellFields_ArmorSelf_IsNotABodyArmorDebuff()
        {
            // same shape as Imperil on the other side of the 0.0 identity
            var shape = MonsterSpellShape.FromSpellFields(ImperilType, 0, 240.0f, DamageType.Undef);

            Assert.IsFalse(shape.IsBodyArmorDebuff);
            Assert.IsFalse(shape.IsVulnerability);
        }

        [TestMethod]
        public void FromSpellFields_FlameBolt_KeepsItsFireElement()
        {
            var shape = ShapeOf(FlameBolt);

            Assert.IsFalse(shape.IsVulnerability);
            Assert.IsFalse(shape.IsBodyArmorDebuff);
            Assert.AreEqual(DamageType.Fire, shape.ElementalDamageType);
        }

        [TestMethod]
        public void FromSpellFields_ResistNether_IsNotTreatedAsAVulnerability()
        {
            // ResistNether (71) sits one past the 64-70 window the predicate accepts, so a Nether
            // resistance debuff is Neutral rather than being mapped onto a damage type
            var shape = MonsterSpellShape.FromSpellFields(VulnType, (uint)PropertyFloat.ResistNether, 2.5f, DamageType.Undef);

            Assert.IsFalse(shape.IsVulnerability);
            Assert.IsFalse(shape.IsBodyArmorDebuff);
        }

        // ------------------------------------------------------------------
        // VulnerabilityProfile.GetMod
        // ------------------------------------------------------------------

        [TestMethod]
        public void GetMod_ReturnsTheIdentityForEverythingOutsideTheSevenResistances()
        {
            var profile = FullyDebuffed;

            Assert.AreEqual(2.5f, profile.GetMod(DamageType.Fire), 1e-6f);
            Assert.AreEqual(2.85f, profile.GetMod(DamageType.Cold), 1e-6f);

            Assert.AreEqual(1.0f, profile.GetMod(DamageType.Undef), 1e-6f);
            Assert.AreEqual(1.0f, profile.GetMod(DamageType.Health), 1e-6f);
            Assert.AreEqual(1.0f, profile.GetMod(DamageType.Nether), 1e-6f);
            Assert.AreEqual(1.0f, profile.GetMod(DamageType.Elemental), 1e-6f);
        }

        [TestMethod]
        public void None_IsTheAllIdentityProfile()
        {
            var profile = VulnerabilityProfile.None;

            foreach (var type in new[] { DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon,
                                         DamageType.Cold, DamageType.Fire, DamageType.Acid, DamageType.Electric })
            {
                Assert.AreEqual(1.0f, profile.GetMod(type), 1e-6f, $"{type}");
            }

            Assert.AreEqual(0, profile.BodyArmorMod);
        }

        // ------------------------------------------------------------------
        // Classify
        // ------------------------------------------------------------------

        [TestMethod]
        public void Classify_VulnerabilityTheTargetLacks_IsPreferred()
        {
            Assert.AreEqual(MonsterSpellPriority.Preferred,
                MonsterSpellSelector.Classify(ShapeOf(FireVulnOtherVI), Clean));
        }

        [TestMethod]
        public void Classify_VulnerabilityAlreadyStandingAtEqualStrength_IsRedundant()
        {
            Assert.AreEqual(MonsterSpellPriority.Redundant,
                MonsterSpellSelector.Classify(ShapeOf(FireVulnOtherVI), WithFireVuln(2.5f)));
        }

        [TestMethod]
        public void Classify_VulnerabilityAlreadyStandingAtGreaterStrength_IsRedundant()
        {
            Assert.AreEqual(MonsterSpellPriority.Redundant,
                MonsterSpellSelector.Classify(ShapeOf(FireVulnOtherVI), WithFireVuln(2.85f)));
        }

        [TestMethod]
        public void Classify_VulnerabilityUpgradingAWeakerOne_IsNeutral()
        {
            // 1.5 already standing, candidate applies 2.5 - a genuine upgrade, but a weaker want than
            // filling an empty slot, so it sits between Preferred and Redundant
            Assert.AreEqual(MonsterSpellPriority.Neutral,
                MonsterSpellSelector.Classify(ShapeOf(FireVulnOtherVI), WithFireVuln(1.5f)));
        }

        [TestMethod]
        public void Classify_DamageMatchingAStandingVulnerability_IsPreferred()
        {
            Assert.AreEqual(MonsterSpellPriority.Preferred,
                MonsterSpellSelector.Classify(ShapeOf(FlameBolt), WithFireVuln(2.0f)));
        }

        [TestMethod]
        public void Classify_DamageWithNoStandingVulnerability_IsNeutral()
        {
            Assert.AreEqual(MonsterSpellPriority.Neutral,
                MonsterSpellSelector.Classify(ShapeOf(FlameBolt), WithFireVuln(1.0f)));
        }

        [TestMethod]
        public void Classify_Harm_IsNeutralEitherWay()
        {
            Assert.AreEqual(MonsterSpellPriority.Neutral,
                MonsterSpellSelector.Classify(ShapeOf(HarmOtherVI), Clean));

            Assert.AreEqual(MonsterSpellPriority.Neutral,
                MonsterSpellSelector.Classify(ShapeOf(HarmOtherVI), FullyDebuffed));
        }

        [TestMethod]
        public void Classify_ImperilOnAnImperiledTarget_IsRedundant()
        {
            Assert.AreEqual(MonsterSpellPriority.Redundant,
                MonsterSpellSelector.Classify(ShapeOf(ImperilOther), FullyDebuffed));
        }

        [TestMethod]
        public void Classify_ImperilOnAnUnimperiledTarget_IsPreferred()
        {
            Assert.AreEqual(MonsterSpellPriority.Preferred,
                MonsterSpellSelector.Classify(ShapeOf(ImperilOther), Clean));
        }

        [TestMethod]
        public void Classify_ImperilOnATargetWhoseNetBodyArmorIsPositive_IsPreferred()
        {
            // deliberate under-detection: BodyArmorMod is the NET mod, so Armor Self can mask a standing
            // Imperil. The monster then casts the debuff again, which is exactly what it did before this
            // code existed - the safe direction. Over-detection would suppress a debuff that never landed.
            var maskedByArmorSelf = new VulnerabilityProfile(1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 15);

            Assert.AreEqual(MonsterSpellPriority.Preferred,
                MonsterSpellSelector.Classify(ShapeOf(ImperilOther), maskedByArmorSelf));
        }

        // ------------------------------------------------------------------
        // probe-order recovery, used by every neutrality test below
        // ------------------------------------------------------------------

        /// <summary>
        /// Recovers the exact sequence of book entries TrySelect rolls, using only its public surface.
        /// Run k = 1, 2, 3... with an rng rigged to succeed on its k-th call and fail on every other:
        /// the spell it returns is by definition the k-th entry it rolled. The first k that yields no
        /// selection is one past the total number of rolls, so the returned list is the complete,
        /// ordered probe sequence.
        /// </summary>
        private static List<int> ProbeOrder(IEnumerable<KeyValuePair<int, float>> book, VulnerabilityProfile profile)
        {
            var order = new List<int>();

            for (var fireOn = 1; fireOn <= 100; fireOn++)
            {
                var calls = 0;

                Func<double> rng = () =>
                {
                    calls++;
                    return calls == fireOn ? 0.0 : 1.0;
                };

                if (!MonsterSpellSelector.TrySelect(book, Shapes, profile, rng, out var spellId))
                    return order;

                order.Add(spellId);
            }

            Assert.Fail("probe recovery did not terminate - TrySelect rolled more than 100 times");
            return order;
        }

        private static readonly Func<double> AlwaysFails = () => 1.0;
        private static readonly Func<double> AlwaysSucceeds = () => 0.0;

        /// <summary>
        /// A realistic caster book: one Imperil, two Vulnerabilities of different elements, a matching
        /// elemental damage spell, a Harm, and the two spells Threads strips at spawn. Probabilities are
        /// distinct and all sit in the base-2.0 encoding, so each maps to a real per-entry chance.
        /// </summary>
        private static Dictionary<int, float> FullBook() => new Dictionary<int, float>
        {
            { ImperilOther,        2.05f },
            { FireVulnOtherVI,     2.06f },
            { GeliditesGift,       2.07f },
            { FlameBolt,           2.08f },
            { HarmOtherVI,         2.09f },
            { ExsanguinatingWave,  2.10f },
            { ExplodingMagma,      2.11f },
        };

        /// <summary>
        /// The same book after Threads' spawn-time strip actually runs over it. Built by calling
        /// DungeonSpellFilter.Strip rather than by hand, so this fixture cannot drift from what a Threads
        /// creature really carries.
        /// </summary>
        private static Dictionary<int, float> PostStripBook()
        {
            var book = FullBook();

            var banned = DungeonSpellFilter.ParseBannedIds(DungeonSpellFilter.DefaultBannedSpellIds);

            Func<int, int> projectilesOf = id => id == ExplodingMagma ? 9 : 1;

            var removed = DungeonSpellFilter.Strip(book, banned, projectilesOf, hasDamagingBodyPart: true);

            // guards the fixture itself: if the strip stopped removing these, the "post-strip" book would
            // silently become a second copy of the full book and this axis would go untested
            CollectionAssert.AreEquivalent(new[] { ExsanguinatingWave, ExplodingMagma }, removed.ToArray());
            Assert.AreEqual(5, book.Count);

            return book;
        }

        // ------------------------------------------------------------------
        // frequency neutrality - the invariant that protects the whole design
        // ------------------------------------------------------------------

        [TestMethod]
        public void TrySelect_FullBook_RollsEveryEntryExactlyOnceInNonDecreasingTierOrder()
        {
            AssertNeutral(FullBook(), Clean);
            AssertNeutral(FullBook(), WithFireVuln(2.5f));
            AssertNeutral(FullBook(), FullyDebuffed);
        }

        [TestMethod]
        public void TrySelect_PostStripBook_RollsEveryEntryExactlyOnceInNonDecreasingTierOrder()
        {
            AssertNeutral(PostStripBook(), Clean);
            AssertNeutral(PostStripBook(), WithFireVuln(2.5f));
            AssertNeutral(PostStripBook(), FullyDebuffed);
        }

        [TestMethod]
        public void TrySelect_BookWhereEveryEntryIsRedundant_StillRollsEveryEntryExactlyOnce()
        {
            // a fully-debuffed target facing a pure-debuff caster: every candidate is useless, and the
            // monster must STILL cast at exactly its old rate rather than falling silent
            var book = new Dictionary<int, float>
            {
                { ImperilOther,    2.05f },
                { FireVulnOtherVI, 2.06f },
                { GeliditesGift,   2.07f },
            };

            foreach (var entry in book)
                Assert.AreEqual(MonsterSpellPriority.Redundant, MonsterSpellSelector.Classify(ShapeOf(entry.Key), FullyDebuffed));

            AssertNeutral(book, FullyDebuffed);
        }

        private static void AssertNeutral(Dictionary<int, float> book, VulnerabilityProfile profile)
        {
            var order = ProbeOrder(book, profile);

            // every entry rolled, none rolled twice, nothing rolled that was not in the book
            CollectionAssert.AreEquivalent(book.Keys.ToArray(), order,
                "TrySelect must roll every book entry exactly once - dropping or repeating one changes P(cast)");

            // tiers ascend and never go back
            var tiers = order.Select(id => (int)MonsterSpellSelector.Classify(ShapeOf(id), profile)).ToList();

            for (var i = 1; i < tiers.Count; i++)
                Assert.IsTrue(tiers[i] >= tiers[i - 1], $"tier went backwards at index {i}: {string.Join(",", tiers)}");

            // and within one tier, book order is preserved
            foreach (var tier in new[] { MonsterSpellPriority.Preferred, MonsterSpellPriority.Neutral, MonsterSpellPriority.Redundant })
            {
                var fromBook = book.Keys.Where(id => MonsterSpellSelector.Classify(ShapeOf(id), profile) == tier).ToList();
                var fromOrder = order.Where(id => MonsterSpellSelector.Classify(ShapeOf(id), profile) == tier).ToList();

                CollectionAssert.AreEqual(fromBook, fromOrder, $"book order not preserved within {tier}");
            }
        }

        [TestMethod]
        public void TrySelect_WithAnRngThatAlwaysFails_SelectsNothing()
        {
            Assert.IsFalse(MonsterSpellSelector.TrySelect(FullBook(), Shapes, Clean, AlwaysFails, out var spellId));
            Assert.AreEqual(0, spellId);
        }

        [TestMethod]
        public void TrySelect_OnAnEmptyBook_SelectsNothing()
        {
            Assert.IsFalse(MonsterSpellSelector.TrySelect(new Dictionary<int, float>(), Shapes, Clean, AlwaysSucceeds, out var spellId));
            Assert.AreEqual(0, spellId);
        }

        // ------------------------------------------------------------------
        // the behaviour the owner actually asked for
        // ------------------------------------------------------------------

        [TestMethod]
        public void TrySelect_PrefersAVulnerabilityTheTargetLacksOverOneItAlreadyCarries()
        {
            // book order puts the already-applied fire vuln FIRST; the cold vuln the target lacks must
            // still win, which is the whole "avoid re-vulning" ask
            var book = new Dictionary<int, float>
            {
                { FireVulnOtherVI, 2.05f },
                { GeliditesGift,   2.05f },
            };

            Assert.IsTrue(MonsterSpellSelector.TrySelect(book, Shapes, WithFireVuln(2.5f), AlwaysSucceeds, out var spellId));
            Assert.AreEqual(GeliditesGift, spellId);
        }

        [TestMethod]
        public void TrySelect_PrefersDamageMatchingAStandingVulnerabilityOverARedundantDebuff()
        {
            // "cast damaging spells if appropriate vulns are in place": the fire vuln is already up, so
            // re-casting it is Redundant and the Flame Bolt is Preferred, despite coming later in the book
            var book = new Dictionary<int, float>
            {
                { FireVulnOtherVI, 2.05f },
                { HarmOtherVI,     2.05f },
                { FlameBolt,       2.05f },
            };

            Assert.IsTrue(MonsterSpellSelector.TrySelect(book, Shapes, WithFireVuln(2.5f), AlwaysSucceeds, out var spellId));
            Assert.AreEqual(FlameBolt, spellId);
        }

        [TestMethod]
        public void TrySelect_OnACleanTarget_PrefersTheDebuffsOverPlainDamage()
        {
            // nothing is up yet, so both debuffs are Preferred and the bolt is only Neutral; book order
            // decides between the two Preferred entries
            var book = new Dictionary<int, float>
            {
                { FlameBolt,     2.05f },
                { ImperilOther,  2.05f },
                { FireVulnOtherVI, 2.05f },
            };

            Assert.IsTrue(MonsterSpellSelector.TrySelect(book, Shapes, Clean, AlwaysSucceeds, out var spellId));
            Assert.AreEqual(ImperilOther, spellId);
        }

        [TestMethod]
        public void TrySelect_WithNothingPreferredOrNeutral_StillCastsTheRedundantSpell()
        {
            var book = new Dictionary<int, float> { { FireVulnOtherVI, 2.05f } };

            Assert.IsTrue(MonsterSpellSelector.TrySelect(book, Shapes, WithFireVuln(2.5f), AlwaysSucceeds, out var spellId));
            Assert.AreEqual(FireVulnOtherVI, spellId);
        }

        // ------------------------------------------------------------------
        // the base-2.0 probability encoding, carried over verbatim
        // ------------------------------------------------------------------

        [TestMethod]
        public void TrySelect_ConvertsTheBaseTwoProbabilityEncodingUnchanged()
        {
            // 2.05 means a 5% chance
            var book = new Dictionary<int, float> { { HarmOtherVI, 2.05f } };

            Assert.IsTrue(MonsterSpellSelector.TrySelect(book, Shapes, Clean, () => 0.0499, out _));
            Assert.IsFalse(MonsterSpellSelector.TrySelect(book, Shapes, Clean, () => 0.0501, out _));
        }

        [TestMethod]
        public void TrySelect_ConvertsARawSubTwoProbabilityByDividingByOneHundred()
        {
            // the uncommon form: a raw value at or below 2.0 is read as a percentage, so 0.5 is 0.5%
            var book = new Dictionary<int, float> { { HarmOtherVI, 0.5f } };

            Assert.IsTrue(MonsterSpellSelector.TrySelect(book, Shapes, Clean, () => 0.0049, out _));
            Assert.IsFalse(MonsterSpellSelector.TrySelect(book, Shapes, Clean, () => 0.0051, out _));
        }

        [TestMethod]
        public void TrySelect_AFlatTwoPointZeroEntryBecomesTwoPercent()
        {
            var book = new Dictionary<int, float> { { HarmOtherVI, 2.0f } };

            Assert.IsTrue(MonsterSpellSelector.TrySelect(book, Shapes, Clean, () => 0.019, out _));
            Assert.IsFalse(MonsterSpellSelector.TrySelect(book, Shapes, Clean, () => 0.021, out _));
        }
    }
}
