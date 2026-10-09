using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Target-aware monster spell selection - "vuln the player if they do not already have that vuln, do not
    /// re-cast a debuff they already carry, cast damage once the vulns are in place".
    ///
    /// Two load-bearing contracts are pinned here:
    ///
    /// 1. HOW OFTEN a monster casts never changes. The original roll is one independent Bernoulli trial per
    ///    book entry, so P(cast at all) = 1 - prod(1 - p_i). A Redundant entry is skipped, and the survivors'
    ///    chances are rescaled so that product is exactly preserved - pinned by a seeded Monte Carlo against
    ///    the analytic rate.
    ///
    /// 2. A debuff the target already carries, in the SAME spell category at equal or greater strength, from
    ///    any caster, is never cast again unless every spell in the book is in that state. The 2026-10-06 owner
    ///    report ("Thread monsters keep casting repeat debuffs") is reproduced end to end against a real
    ///    Creature's enchantment registry, including Armor Self VIII sitting beside the Imperil.
    ///
    /// The probe order is recovered through the public API alone: TrySelect is run once per k with an rng
    /// rigged to succeed on its k-th call and fail on every other, so the spell it returns IS the k-th entry it
    /// rolled.
    ///
    /// stat_Mod_Type / stat_Mod_Key / stat_Mod_Val below are as ace_world stores them (prod spell table,
    /// queried 2026-10-06). SpellCategory is a client-DAT field, not in ace_world. The fixture categories are the
    /// DAT ones: every Imperil Other (and Gossamer Flesh) is ArmorLowering (116) and Armor Self is ArmorRaising
    /// (115), per the catalog in PvpRules.VulnerabilityCategories. They are NOT ArmorValueLowering (161), which
    /// is the synthetic category MonsterEffects' DebuffEffect tags its own Imperil with - so a real Imperil and
    /// a DebuffEffect Imperil sit in different categories, stack in the engine, and never cover each other here.
    /// Classification only compares categories for EQUALITY, and production reads each spell's category from
    /// the DAT through Spell.Category.
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
        // stat_Mod_Val -225. Armor Self I-VI are the same type on the other side of the 0.0 identity.
        private const EnchantmentTypeFlags ImperilType =
            EnchantmentTypeFlags.BodyArmorValue | EnchantmentTypeFlags.MultipleStat | EnchantmentTypeFlags.Additive;

        // Incantation of Armor Self (4291): stat_Mod_Type 33595520 = 41088 | Beneficial, stat_Mod_Val 250.
        private const EnchantmentTypeFlags ArmorSelfIncantationType = ImperilType | EnchantmentTypeFlags.Beneficial;

        // Weakness Other VI (1343): stat_Mod_Type 36865, stat_Mod_Key 1 (Strength), stat_Mod_Val -35.
        // Strength Other VI (1337) is the SAME type and key at +35, so only the sign separates them.
        private const EnchantmentTypeFlags AttributeType =
            EnchantmentTypeFlags.Attribute | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive;

        private const uint ResistFireKey = (uint)PropertyFloat.ResistFire;      // 67
        private const uint ResistColdKey = (uint)PropertyFloat.ResistCold;      // 68
        private const uint StrengthKey = (uint)PropertyAttribute.Strength;      // 1

        // stacking groups
        private const SpellCategory FireVulnCat = SpellCategory.FireVulnerability;
        private const SpellCategory ColdVulnCat = SpellCategory.ColdVulnerability;
        private const SpellCategory ImperilCat = SpellCategory.ArmorLowering;      // 116, DAT category of Imperil
        private const SpellCategory ArmorSelfCat = SpellCategory.ArmorRaising;     // 115, DAT category of Armor Self
        private const SpellCategory WeaknessCat = SpellCategory.StrengthLowering;
        private static SpellCategory HuntersMarkCat => (SpellCategory)EnchantmentManager.SpellCategory_ClassAbility_HuntersMark;

        // spell ids used in the fixtures
        private const int FireVulnOtherVI = 1108;       // fire vuln, stat_Mod_Val 2.5
        private const int GeliditesGift = 2168;         // COLD vuln, stat_Mod_Val 2.85 - see below
        private const int ImperilOther = 4312;          // body-armor debuff, stat_Mod_Val -225
        private const int WeaknessOtherVI = 1343;       // Strength -35
        private const int WeaknessOtherIII = 1340;      // Strength -20
        private const int StrengthOtherVI = 1337;       // Strength +35, the buff twin
        private const int FlameBolt = 4439;             // fire damage, e_Type 16, 1 projectile
        private const int HarmOtherVI = 1176;           // no stat mod at all, e_Type NULL -> Undef
        private const int ExsanguinatingWave = 3940;    // banned by id; e_Type 128 (Health), 1 projectile
        private const int ExplodingMagma = 1781;        // stripped by the projectile rule; e_Type 16, 9 projectiles

        private static MonsterSpellShape ShapeOf(int spellId)
        {
            switch (spellId)
            {
                case FireVulnOtherVI:
                    return MonsterSpellShape.FromSpellFields(VulnType, ResistFireKey, 2.5f, DamageType.Undef, FireVulnCat);
                case GeliditesGift:
                    return MonsterSpellShape.FromSpellFields(VulnType, ResistColdKey, 2.85f, DamageType.Undef, ColdVulnCat);
                case ImperilOther:
                    return MonsterSpellShape.FromSpellFields(ImperilType, 0, -225.0f, DamageType.Undef, ImperilCat);
                case WeaknessOtherVI:
                    return MonsterSpellShape.FromSpellFields(AttributeType, StrengthKey, -35.0f, DamageType.Undef, WeaknessCat);
                case WeaknessOtherIII:
                    return MonsterSpellShape.FromSpellFields(AttributeType, StrengthKey, -20.0f, DamageType.Undef, WeaknessCat);
                case StrengthOtherVI:
                    return MonsterSpellShape.FromSpellFields(AttributeType, StrengthKey, 35.0f, DamageType.Undef, SpellCategory.StrengthRaising);
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
        // carried top layers and target snapshots
        // ------------------------------------------------------------------

        private static KeyValuePair<SpellCategory, CarriedEnchantment> Carried(SpellCategory category, EnchantmentTypeFlags type, uint key, float value) =>
            new KeyValuePair<SpellCategory, CarriedEnchantment>(category, new CarriedEnchantment(type, key, value, 250));

        private static KeyValuePair<SpellCategory, CarriedEnchantment> CarriedFireVuln(float v) => Carried(FireVulnCat, VulnType, ResistFireKey, v);
        private static KeyValuePair<SpellCategory, CarriedEnchantment> CarriedColdVuln(float v) => Carried(ColdVulnCat, VulnType, ResistColdKey, v);
        private static KeyValuePair<SpellCategory, CarriedEnchantment> CarriedImperil(float v) => Carried(ImperilCat, ImperilType, 0, v);
        private static KeyValuePair<SpellCategory, CarriedEnchantment> CarriedArmorSelf(float v) => Carried(ArmorSelfCat, ArmorSelfIncantationType, 0, v);
        private static KeyValuePair<SpellCategory, CarriedEnchantment> CarriedWeakness(float v) => Carried(WeaknessCat, AttributeType, StrengthKey, v);

        /// <summary>
        /// A profile whose float/int aggregates are consistent with its layers, the way BuildVulnerabilityProfile
        /// derives both from one registry.
        /// </summary>
        private static VulnerabilityProfile Profile(params KeyValuePair<SpellCategory, CarriedEnchantment>[] layers)
        {
            float fire = 1.0f, cold = 1.0f;
            var negArmor = 0;

            foreach (var l in layers)
            {
                var c = l.Value;
                if (c.StatModType.HasFlag(EnchantmentTypeFlags.Multiplicative) && c.StatModValue > 1.0f)
                {
                    if (c.StatModKey == ResistFireKey) fire *= c.StatModValue;
                    if (c.StatModKey == ResistColdKey) cold *= c.StatModValue;
                }
                if (c.StatModType.HasFlag(EnchantmentTypeFlags.BodyArmorValue) && c.StatModValue < 0.0f)
                    negArmor += (int)c.StatModValue;
            }

            return new VulnerabilityProfile(1.0f, 1.0f, 1.0f, cold, fire, 1.0f, 1.0f, negArmor,
                layers.ToDictionary(l => l.Key, l => l.Value));
        }

        private static VulnerabilityProfile Clean => VulnerabilityProfile.None;

        private static VulnerabilityProfile WithFireVuln(float mod) => Profile(CarriedFireVuln(mod));

        /// <summary>
        /// Every debuff in the fixture book already standing at full strength, plus a self-cast Armor Self VIII.
        /// </summary>
        private static VulnerabilityProfile FullyDebuffed => Profile(
            CarriedFireVuln(2.5f), CarriedColdVuln(2.85f), CarriedImperil(-225.0f), CarriedArmorSelf(250.0f), CarriedWeakness(-35.0f));

        private static readonly int[] DebuffIds = { FireVulnOtherVI, GeliditesGift, ImperilOther, WeaknessOtherVI };

        // ------------------------------------------------------------------
        // the flag constants themselves
        // ------------------------------------------------------------------

        [TestMethod]
        public void EnchantmentTypeFlagCombinations_MatchTheStoredStatModTypes()
        {
            // if these drift, every shape assertion below is testing a different spell than it claims to
            Assert.AreEqual(20488, (int)VulnType);
            Assert.AreEqual(41088, (int)ImperilType);
            Assert.AreEqual(33595520, (int)ArmorSelfIncantationType);
            Assert.AreEqual(36865, (int)AttributeType);
            Assert.AreEqual(67u, ResistFireKey);
            Assert.AreEqual(68u, ResistColdKey);
            Assert.AreEqual(1u, StrengthKey);
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
            var shape = ShapeOf(FireVulnOtherVI);

            Assert.IsTrue(shape.IsVulnerability);
            Assert.IsTrue(shape.IsDebuff);
            Assert.AreEqual(DamageType.Fire, shape.VulnElement);
            Assert.AreEqual(2.5f, shape.VulnStatModVal, 1e-6f);
            Assert.AreEqual(FireVulnCat, shape.Category);
            Assert.IsFalse(shape.IsBodyArmorDebuff);
        }

        [TestMethod]
        public void FromSpellFields_FireProtection_IsNotAVulnerability()
        {
            // identical stat_Mod_Type and stat_Mod_Key to Fire Vulnerability; only the value differs
            var shape = MonsterSpellShape.FromSpellFields(VulnType, ResistFireKey, 0.4f, DamageType.Undef);

            Assert.IsFalse(shape.IsVulnerability);
            Assert.IsFalse(shape.IsDebuff);
            Assert.AreEqual(DamageType.Undef, shape.VulnElement);
        }

        [TestMethod]
        public void FromSpellFields_GeliditesGift_IsAColdVulnerabilityDespiteItsName()
        {
            // Gelidite's Gift (spell 2168): stat_Mod_Key 68 (ResistCold), stat_Mod_Val 2.85. The case a
            // name-based or id-list-based predicate gets wrong and the shape predicate gets right.
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
            Assert.IsTrue(shape.IsDebuff);
            Assert.IsFalse(shape.IsVulnerability);
        }

        [TestMethod]
        public void FromSpellFields_ArmorSelf_IsNotADebuff()
        {
            var shape = MonsterSpellShape.FromSpellFields(ArmorSelfIncantationType, 0, 250.0f, DamageType.Undef, ArmorSelfCat);

            Assert.IsFalse(shape.IsBodyArmorDebuff);
            Assert.IsFalse(shape.IsDebuff);
        }

        [TestMethod]
        public void FromSpellFields_WeaknessIsAnAttributeDebuff_AndStrengthIsNot()
        {
            Assert.IsTrue(ShapeOf(WeaknessOtherVI).IsAttributeSkillOrVitalDebuff);
            Assert.IsTrue(ShapeOf(WeaknessOtherVI).IsDebuff);

            // same stat_Mod_Type and key, opposite sign
            Assert.IsFalse(ShapeOf(StrengthOtherVI).IsAttributeSkillOrVitalDebuff);
            Assert.IsFalse(ShapeOf(StrengthOtherVI).IsDebuff);
        }

        [TestMethod]
        public void FromSpellFields_SkillAndVitalDebuffs_AreDebuffs()
        {
            var skill = MonsterSpellShape.FromSpellFields(EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive,
                (uint)Skill.MeleeDefense, -35.0f, DamageType.Undef, SpellCategory.MeleeDefenseLowering);
            var vital = MonsterSpellShape.FromSpellFields(EnchantmentTypeFlags.SecondAtt | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative,
                1, 0.8f, DamageType.Undef);

            Assert.IsTrue(skill.IsAttributeSkillOrVitalDebuff);
            Assert.IsTrue(vital.IsAttributeSkillOrVitalDebuff);
        }

        [TestMethod]
        public void FromSpellFields_FlameBolt_KeepsItsFireElement()
        {
            var shape = ShapeOf(FlameBolt);

            Assert.IsFalse(shape.IsDebuff);
            Assert.AreEqual(DamageType.Fire, shape.ElementalDamageType);
        }

        [TestMethod]
        public void FromSpellFields_ResistNether_IsNotTreatedAsAVulnerability()
        {
            // ResistNether (71) sits one past the 64-70 window the predicate accepts
            var shape = MonsterSpellShape.FromSpellFields(VulnType, (uint)PropertyFloat.ResistNether, 2.5f, DamageType.Undef);

            Assert.IsFalse(shape.IsVulnerability);
            Assert.IsFalse(shape.IsBodyArmorDebuff);
        }

        // ------------------------------------------------------------------
        // VulnerabilityProfile
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

            Assert.AreEqual(0, profile.NegativeBodyArmorMod);
            Assert.IsNull(profile.TopLayers);
        }

        [TestMethod]
        public void BuildTopLayers_KeepsTheHighestPowerEntryPerCategory_AndSkipsCooldowns()
        {
            var entries = new List<PropertiesEnchantmentRegistry>
            {
                new PropertiesEnchantmentRegistry { SpellCategory = WeaknessCat, PowerLevel = 150, StatModType = AttributeType, StatModKey = StrengthKey, StatModValue = -20f, LayerId = 1 },
                new PropertiesEnchantmentRegistry { SpellCategory = WeaknessCat, PowerLevel = 300, StatModType = AttributeType, StatModKey = StrengthKey, StatModValue = -35f, LayerId = 2 },
                new PropertiesEnchantmentRegistry { SpellCategory = WeaknessCat, PowerLevel = 200, StatModType = AttributeType, StatModKey = StrengthKey, StatModValue = -25f, LayerId = 3 },
                new PropertiesEnchantmentRegistry { SpellCategory = ImperilCat, PowerLevel = 1, StatModType = EnchantmentTypeFlags.Cooldown, StatModValue = -999f },
            };

            var layers = VulnerabilityProfile.BuildTopLayers(entries);

            Assert.AreEqual(1, layers.Count, "the cooldown is not a stacking layer");
            Assert.AreEqual(-35f, layers[WeaknessCat].StatModValue, 1e-6f, "the engine applies the highest PowerLevel in a category");
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
            Assert.AreEqual(MonsterSpellPriority.Neutral,
                MonsterSpellSelector.Classify(ShapeOf(FireVulnOtherVI), WithFireVuln(1.5f)));
        }

        [TestMethod]
        public void Classify_VulnerabilityStandingInADifferentCategory_StacksSoIsNotRedundant()
        {
            // a stronger fire vuln in some OTHER category multiplies with this one in the engine, so this cast
            // is not wasted - it may not be Preferred (the element is already up), but it is never skipped
            var profile = Profile(Carried(SpellCategory.FireWardVulnerability, VulnType, ResistFireKey, 3.0f));

            Assert.AreEqual(MonsterSpellPriority.Neutral,
                MonsterSpellSelector.Classify(ShapeOf(FireVulnOtherVI), profile));
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
                MonsterSpellSelector.Classify(ShapeOf(FlameBolt), Clean));
        }

        [TestMethod]
        public void Classify_Harm_IsNeutralEitherWay()
        {
            Assert.AreEqual(MonsterSpellPriority.Neutral, MonsterSpellSelector.Classify(ShapeOf(HarmOtherVI), Clean));
            Assert.AreEqual(MonsterSpellPriority.Neutral, MonsterSpellSelector.Classify(ShapeOf(HarmOtherVI), FullyDebuffed));
        }

        [TestMethod]
        public void Classify_ImperilOnAnImperiledTarget_IsRedundant()
        {
            Assert.AreEqual(MonsterSpellPriority.Redundant,
                MonsterSpellSelector.Classify(ShapeOf(ImperilOther), Profile(CarriedImperil(-225.0f))));
        }

        [TestMethod]
        public void Classify_ImperilOnAnUnimperiledTarget_IsPreferred()
        {
            Assert.AreEqual(MonsterSpellPriority.Preferred,
                MonsterSpellSelector.Classify(ShapeOf(ImperilOther), Clean));

            // Armor Self alone is a buff, not an Imperil
            Assert.AreEqual(MonsterSpellPriority.Preferred,
                MonsterSpellSelector.Classify(ShapeOf(ImperilOther), Profile(CarriedArmorSelf(250.0f))));
        }

        [TestMethod]
        public void Classify_ArmorSelfNoLongerMasksAStandingImperil()
        {
            // replaces the #1042 pin "net body armor positive -> Preferred". Armor Self VIII (+250) beside
            // Imperil (-225) netted +25 and the monster re-cast Imperil FIRST on every roll.
            Assert.AreEqual(MonsterSpellPriority.Redundant,
                MonsterSpellSelector.Classify(ShapeOf(ImperilOther), FullyDebuffed));
        }

        [TestMethod]
        public void Classify_ImperilUpgradingAWeakerImperilUnderArmorSelf_IsNeutralNotPreferred()
        {
            // Imperil IV (-100) under Armor Self VIII: the stronger Imperil is a real upgrade, so it is not
            // skipped, but an Imperil IS standing, so it is not promoted either
            Assert.AreEqual(MonsterSpellPriority.Neutral,
                MonsterSpellSelector.Classify(ShapeOf(ImperilOther), Profile(CarriedImperil(-100.0f), CarriedArmorSelf(250.0f))));
        }

        [TestMethod]
        public void Classify_AttributeDebuff_RedundantWhenCarried_NeutralWhenAbsent()
        {
            Assert.AreEqual(MonsterSpellPriority.Redundant,
                MonsterSpellSelector.Classify(ShapeOf(WeaknessOtherVI), Profile(CarriedWeakness(-35.0f))));
            Assert.AreEqual(MonsterSpellPriority.Neutral,
                MonsterSpellSelector.Classify(ShapeOf(WeaknessOtherVI), Clean));
        }

        [TestMethod]
        public void Classify_AWeakerCarriedDebuffDoesNotBlockAStrongerOne()
        {
            // discriminating test (4): Weakness III (-20) standing must not make Weakness VI (-35) Redundant,
            // and the reverse case must
            Assert.AreEqual(MonsterSpellPriority.Neutral,
                MonsterSpellSelector.Classify(ShapeOf(WeaknessOtherVI), Profile(CarriedWeakness(-20.0f))));
            Assert.AreEqual(MonsterSpellPriority.Redundant,
                MonsterSpellSelector.Classify(ShapeOf(WeaknessOtherIII), Profile(CarriedWeakness(-35.0f))));

            Assert.AreEqual(MonsterSpellPriority.Neutral,
                MonsterSpellSelector.Classify(ShapeOf(ImperilOther), Profile(CarriedImperil(-100.0f))));
            Assert.AreEqual(MonsterSpellPriority.Neutral,
                MonsterSpellSelector.Classify(ShapeOf(FireVulnOtherVI), WithFireVuln(1.5f)));
        }

        [TestMethod]
        public void Classify_HuntersMarkCategoryNeverMakesACandidateRedundant()
        {
            // Hunter's Mark is its own stacking debuff in a class-ability category (#1125). Even an entry there
            // with the candidate's exact type, key and a stronger value stacks rather than covers.
            var markAsAttribute = Profile(Carried(HuntersMarkCat, AttributeType, StrengthKey, -50.0f));
            var markAsVuln = Profile(Carried(HuntersMarkCat, VulnType, ResistFireKey, 3.0f));

            Assert.AreEqual(MonsterSpellPriority.Neutral, MonsterSpellSelector.Classify(ShapeOf(WeaknessOtherVI), markAsAttribute));
            Assert.AreNotEqual(MonsterSpellPriority.Redundant, MonsterSpellSelector.Classify(ShapeOf(FireVulnOtherVI), markAsVuln));
        }

        [TestMethod]
        public void Classify_ADebuffWithNoCategory_IsNeverRedundant()
        {
            var noCategory = MonsterSpellShape.FromSpellFields(AttributeType, StrengthKey, -35.0f, DamageType.Undef);

            Assert.AreEqual(MonsterSpellPriority.Neutral, MonsterSpellSelector.Classify(noCategory, FullyDebuffed));
        }

        // ------------------------------------------------------------------
        // probe-order recovery
        // ------------------------------------------------------------------

        /// <summary>
        /// Recovers the exact sequence of book entries TrySelect rolls, using only its public surface.
        /// Run k = 1, 2, 3... with an rng rigged to succeed on its k-th call and fail on every other: the
        /// spell it returns is by definition the k-th entry it rolled. Every rescaled probability in these
        /// fixtures is above zero, so a 0.0 draw always hits.
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
        /// A realistic caster book: one Imperil, two Vulnerabilities of different elements, a Weakness, a
        /// matching elemental damage spell, a Harm, and the two spells Threads strips at spawn.
        /// </summary>
        private static Dictionary<int, float> FullBook() => new Dictionary<int, float>
        {
            { ImperilOther,        2.05f },
            { FireVulnOtherVI,     2.06f },
            { GeliditesGift,       2.07f },
            { WeaknessOtherVI,     2.05f },
            { FlameBolt,           2.08f },
            { HarmOtherVI,         2.09f },
            { ExsanguinatingWave,  2.10f },
            { ExplodingMagma,      2.11f },
        };

        /// <summary>
        /// The same book after Threads' spawn-time strip actually runs over it, built by calling
        /// DungeonSpellFilter.Strip so this fixture cannot drift from what a Threads creature carries.
        /// </summary>
        private static Dictionary<int, float> PostStripBook()
        {
            var book = FullBook();

            var banned = DungeonSpellFilter.ParseBannedIds(DungeonSpellFilter.DefaultBannedSpellIds);

            Func<int, int> projectilesOf = id => id == ExplodingMagma ? 9 : 1;

            var removed = DungeonSpellFilter.Strip(book, banned, projectilesOf, hasDamagingBodyPart: true);

            CollectionAssert.AreEquivalent(new[] { ExsanguinatingWave, ExplodingMagma }, removed.ToArray());
            Assert.AreEqual(6, book.Count);

            return book;
        }

        /// <summary>
        /// Every non-Redundant entry rolled exactly once, no Redundant entry rolled at all, tiers ascending,
        /// book order kept inside a tier.
        /// </summary>
        private static void AssertRollsSurvivorsOnceInTierOrder(Dictionary<int, float> book, VulnerabilityProfile profile)
        {
            var order = ProbeOrder(book, profile);

            var survivors = book.Keys.Where(id => MonsterSpellSelector.Classify(ShapeOf(id), profile) != MonsterSpellPriority.Redundant).ToArray();

            CollectionAssert.AreEquivalent(survivors, order,
                "every surviving entry must be rolled exactly once, and no Redundant entry at all");

            var tiers = order.Select(id => (int)MonsterSpellSelector.Classify(ShapeOf(id), profile)).ToList();

            for (var i = 1; i < tiers.Count; i++)
                Assert.IsTrue(tiers[i] >= tiers[i - 1], $"tier went backwards at index {i}: {string.Join(",", tiers)}");

            foreach (var tier in new[] { MonsterSpellPriority.Preferred, MonsterSpellPriority.Neutral })
            {
                var fromBook = book.Keys.Where(id => MonsterSpellSelector.Classify(ShapeOf(id), profile) == tier).ToList();
                var fromOrder = order.Where(id => MonsterSpellSelector.Classify(ShapeOf(id), profile) == tier).ToList();

                CollectionAssert.AreEqual(fromBook, fromOrder, $"book order not preserved within {tier}");
            }
        }

        [TestMethod]
        public void TrySelect_FullBook_RollsSurvivorsOnceAndSkipsRedundant()
        {
            AssertRollsSurvivorsOnceInTierOrder(FullBook(), Clean);
            AssertRollsSurvivorsOnceInTierOrder(FullBook(), WithFireVuln(2.5f));
            AssertRollsSurvivorsOnceInTierOrder(FullBook(), FullyDebuffed);
        }

        [TestMethod]
        public void TrySelect_PostStripBook_RollsSurvivorsOnceAndSkipsRedundant()
        {
            AssertRollsSurvivorsOnceInTierOrder(PostStripBook(), Clean);
            AssertRollsSurvivorsOnceInTierOrder(PostStripBook(), WithFireVuln(2.5f));
            AssertRollsSurvivorsOnceInTierOrder(PostStripBook(), FullyDebuffed);
        }

        [TestMethod]
        public void TrySelect_AllRedundant_FallsBackToTheOriginalFlatRoll()
        {
            // discriminating test (5): a pure-debuff caster facing a target that carries all of it must still
            // cast at exactly its old rate, so the whole book is rolled once, in BOOK order, at the original
            // probabilities
            var book = new Dictionary<int, float>
            {
                { WeaknessOtherVI, 2.05f },
                { ImperilOther,    2.05f },
                { FireVulnOtherVI, 2.06f },
                { GeliditesGift,   2.07f },
            };

            foreach (var entry in book)
                Assert.AreEqual(MonsterSpellPriority.Redundant, MonsterSpellSelector.Classify(ShapeOf(entry.Key), FullyDebuffed));

            CollectionAssert.AreEqual(book.Keys.ToList(), ProbeOrder(book, FullyDebuffed), "all-Redundant must be the flat book-order roll");

            // and at the ORIGINAL per-entry chance: 2.05 is 5%
            var single = new Dictionary<int, float> { { FireVulnOtherVI, 2.05f } };

            Assert.IsTrue(MonsterSpellSelector.TrySelect(single, Shapes, WithFireVuln(2.5f), () => 0.0499, out var hit));
            Assert.AreEqual(FireVulnOtherVI, hit);
            Assert.IsFalse(MonsterSpellSelector.TrySelect(single, Shapes, WithFireVuln(2.5f), () => 0.0501, out _));
        }

        [TestMethod]
        public void TrySelect_SurvivorsWithZeroChance_FallBackToTheFlatRoll()
        {
            // a survivor at 0% cannot carry any rate, so skipping would silence the monster
            var book = new Dictionary<int, float> { { FireVulnOtherVI, 2.05f }, { HarmOtherVI, 0.0f } };

            CollectionAssert.AreEqual(new List<int> { FireVulnOtherVI }, ProbeOrder(book, WithFireVuln(2.5f)));
        }

        [TestMethod]
        public void TrySelect_WithAnRngThatAlwaysFails_SelectsNothing()
        {
            Assert.IsFalse(MonsterSpellSelector.TrySelect(FullBook(), Shapes, FullyDebuffed, AlwaysFails, out var spellId));
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
            var book = new Dictionary<int, float>
            {
                { FlameBolt,       2.05f },
                { ImperilOther,    2.05f },
                { FireVulnOtherVI, 2.05f },
            };

            Assert.IsTrue(MonsterSpellSelector.TrySelect(book, Shapes, Clean, AlwaysSucceeds, out var spellId));
            Assert.AreEqual(ImperilOther, spellId);
        }

        // ------------------------------------------------------------------
        // the base-2.0 probability encoding, carried over verbatim
        // ------------------------------------------------------------------

        [TestMethod]
        public void TrySelect_ConvertsTheBaseTwoProbabilityEncodingUnchanged()
        {
            // 2.05 means a 5%; with nothing Redundant the original float probability is compared directly
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

        [TestMethod]
        public void TrySelect_RescaledSurvivorChance_IsTheClosedFormHazardScale()
        {
            // book: Fire Vuln 10% (Redundant), Harm 10% (survivor). Q_all = 0.81, Q_surv = 0.9, so the Harm's
            // miss chance becomes 0.9^(ln 0.81 / ln 0.9) = 0.9^2 = 0.81 and its hit chance 19%
            var book = new Dictionary<int, float> { { FireVulnOtherVI, 2.10f }, { HarmOtherVI, 2.10f } };

            Assert.IsTrue(MonsterSpellSelector.TrySelect(book, Shapes, WithFireVuln(2.5f), () => 0.1899, out var id));
            Assert.AreEqual(HarmOtherVI, id);
            Assert.IsFalse(MonsterSpellSelector.TrySelect(book, Shapes, WithFireVuln(2.5f), () => 0.1901, out _));
        }

        // ------------------------------------------------------------------
        // exact per-roll probabilities, recovered deterministically
        // ------------------------------------------------------------------

        /// <summary>
        /// Recovers the probability TrySelect actually applies at each roll position, with no randomness.
        /// The number of positions is the number of draws an rng returning 1.0 receives (1.0 never hits, since
        /// every applied probability is at most 1). For position k, an rng returning u on call k and 1.0 on
        /// every other call makes TrySelect select exactly when u is below the k-th applied probability, so
        /// bisecting u recovers it to double precision.
        /// </summary>
        private static List<double> AppliedProbabilities(Dictionary<int, float> book, VulnerabilityProfile profile)
        {
            var positions = 0;
            MonsterSpellSelector.TrySelect(book, Shapes, profile, () => { positions++; return 1.0; }, out _);

            var applied = new List<double>();

            for (var k = 1; k <= positions; k++)
            {
                double lo = 0.0, hi = 1.0;

                for (var iter = 0; iter < 80; iter++)
                {
                    var mid = (lo + hi) / 2.0;
                    var calls = 0;
                    var position = k;

                    if (MonsterSpellSelector.TrySelect(book, Shapes, profile, () => { calls++; return calls == position ? mid : 1.0; }, out _))
                        lo = mid;
                    else
                        hi = mid;
                }

                applied.Add((lo + hi) / 2.0);
            }

            return applied;
        }

        private static double OriginalCastRate(Dictionary<int, float> book) =>
            1.0 - book.Values.Aggregate(1.0, (acc, v) => acc * (1.0 - Math.Clamp((double)MonsterSpellSelector.ToProbability(v), 0.0, 1.0)));

        private static void AssertExactCastRate(string name, Dictionary<int, float> book, VulnerabilityProfile profile)
        {
            var applied = AppliedProbabilities(book, profile);

            foreach (var p in applied)
                Assert.IsTrue(p >= 0.0 && p <= 1.0, $"{name}: applied probability {p} outside [0, 1]");

            var rate = 1.0 - applied.Aggregate(1.0, (acc, p) => acc * (1.0 - p));

            Assert.AreEqual(OriginalCastRate(book), rate, 1e-9, $"{name}: P(cast) {rate} drifted from the original roll ({string.Join(", ", applied)})");
        }

        /// <summary>
        /// The overall share of casts each rolled entry receives, in roll order: p'_k times the chance every
        /// earlier entry missed.
        /// </summary>
        private static List<double> Shares(List<double> applied)
        {
            var shares = new List<double>();
            var reach = 1.0;

            foreach (var p in applied)
            {
                shares.Add(reach * p);
                reach *= 1.0 - p;
            }

            return shares;
        }

        [TestMethod]
        public void ExactRate_CastRateIsPreservedExactly_AcrossEdgeCaseBooks()
        {
            // no Redundant entry, with a p = 0 entry and a p > 1 entry (20.0 encodes 1800%, clamped to 1)
            AssertExactCastRate("no redundant, p=0 and p=20", new Dictionary<int, float> { { HarmOtherVI, 2.05f }, { FlameBolt, 0.0f }, { GeliditesGift, 20.0f } }, WithFireVuln(2.5f));

            // the hazard rescale, with a p = 0 survivor riding along
            AssertExactCastRate("rescale with a p=0 survivor", new Dictionary<int, float> { { FireVulnOtherVI, 2.10f }, { HarmOtherVI, 0.0f }, { FlameBolt, 2.10f } }, WithFireVuln(2.5f));

            // a single survivor
            AssertExactCastRate("single survivor", new Dictionary<int, float> { { FireVulnOtherVI, 2.10f }, { HarmOtherVI, 2.10f } }, WithFireVuln(2.5f));

            // a guaranteed SURVIVOR (p = 1): it already casts every time
            AssertExactCastRate("p=1 survivor", new Dictionary<int, float> { { FireVulnOtherVI, 2.10f }, { HarmOtherVI, 3.0f }, { FlameBolt, 2.05f } }, WithFireVuln(2.5f));

            // a guaranteed REDUNDANT entry (p = 1, and p = 20), no guaranteed survivor: the proportional share-out
            AssertExactCastRate("p=1 redundant", new Dictionary<int, float> { { FireVulnOtherVI, 3.0f }, { HarmOtherVI, 2.05f }, { FlameBolt, 2.05f } }, WithFireVuln(2.5f));
            AssertExactCastRate("p=20 redundant", new Dictionary<int, float> { { FireVulnOtherVI, 20.0f }, { HarmOtherVI, 2.05f }, { FlameBolt, 2.10f } }, WithFireVuln(2.5f));

            // all Redundant: the original flat roll
            AssertExactCastRate("all redundant", new Dictionary<int, float> { { FireVulnOtherVI, 2.05f }, { GeliditesGift, 3.0f } }, FullyDebuffed);

            // the full book against every carried state
            AssertExactCastRate("full book, clean", FullBook(), Clean);
            AssertExactCastRate("full book, fully debuffed", FullBook(), FullyDebuffed);
        }

        [TestMethod]
        public void ExactRate_RedundantGuaranteedEntry_SharesTheCastByOriginalWeight()
        {
            // F1: a Redundant p = 1 entry removed, survivors not guaranteed. The book always cast, so the
            // survivors must too - but split by their ORIGINAL weights, not "whoever rolls first takes all".
            var equal = new Dictionary<int, float> { { FireVulnOtherVI, 3.0f }, { HarmOtherVI, 2.05f }, { FlameBolt, 2.05f } };

            var shares = Shares(AppliedProbabilities(equal, WithFireVuln(2.5f)));

            Assert.AreEqual(2, shares.Count, "both survivors are rolled, the Redundant entry is not");
            Assert.AreEqual(0.5, shares[0], 1e-9, "B:C weights 1:1 must split the casts evenly");
            Assert.AreEqual(0.5, shares[1], 1e-9, "B:C weights 1:1 must split the casts evenly");

            // unequal weights 5% : 10%, through the p = 20 encoding of the guaranteed entry. Roll order is Flame
            // Bolt (Preferred, the fire vuln is up) then Harm (Neutral).
            var unequal = new Dictionary<int, float> { { FireVulnOtherVI, 20.0f }, { HarmOtherVI, 2.05f }, { FlameBolt, 2.10f } };

            CollectionAssert.AreEqual(new List<int> { FlameBolt, HarmOtherVI }, ProbeOrder(unequal, WithFireVuln(2.5f)));

            var unequalShares = Shares(AppliedProbabilities(unequal, WithFireVuln(2.5f)));

            Assert.AreEqual(0.10 / 0.15, unequalShares[0], 1e-6, "Flame Bolt's share must be its weight over the survivors' total");
            Assert.AreEqual(0.05 / 0.15, unequalShares[1], 1e-6, "Harm's share must be its weight over the survivors' total");
        }

        // ------------------------------------------------------------------
        // seeded simulation: the owner report, and the cast-rate invariant
        // ------------------------------------------------------------------

        private const int Trials = 200000;

        /// <summary>
        /// A Threads-style caster after the AoE strip: four debuffs, a fire bolt and a Harm, every entry 10%.
        /// </summary>
        private static Dictionary<int, float> ThreadCasterBook() => new Dictionary<int, float>
        {
            { ImperilOther,    2.10f },
            { FireVulnOtherVI, 2.10f },
            { GeliditesGift,   2.10f },
            { WeaknessOtherVI, 2.10f },
            { FlameBolt,       2.10f },
            { HarmOtherVI,     2.10f },
        };

        private static Dictionary<int, int> Simulate(Dictionary<int, float> book, VulnerabilityProfile profile, int seed)
        {
            var random = new Random(seed);
            Func<double> rng = () => random.NextDouble();

            var picks = new Dictionary<int, int>();

            for (var i = 0; i < Trials; i++)
            {
                MonsterSpellSelector.TrySelect(book, Shapes, profile, rng, out var id);
                picks[id] = picks.TryGetValue(id, out var n) ? n + 1 : 1;
            }

            return picks;
        }

        private static PropertiesEnchantmentRegistry Entry(int spellId, SpellCategory category, uint power, EnchantmentTypeFlags type, uint key, float value) =>
            new PropertiesEnchantmentRegistry
            {
                SpellId = spellId,
                SpellCategory = category,
                LayerId = 1,
                PowerLevel = power,
                StartTime = 0,
                Duration = 60.0,
                StatModType = type,
                StatModKey = key,
                StatModValue = value,
                EnchantmentCategory = (uint)SpellType.Enchantment,
            };

        /// <summary>
        /// A real Creature carrying every debuff in <see cref="ThreadCasterBook"/> plus a self-cast Armor Self
        /// VIII, snapshotted through the production BuildVulnerabilityProfile - so this exercises the
        /// negative-only body-armor read and the registry top-layer copy, not a hand-built profile.
        /// </summary>
        private static VulnerabilityProfile BuffedAndFullyDebuffedTarget()
        {
            var target = TestCreatures.CreateDefender();
            var registry = target.Biota.PropertiesEnchantmentRegistry;
            var rwLock = target.BiotaDatabaseLock;

            registry.AddEnchantment(Entry(FireVulnOtherVI, FireVulnCat, 300, VulnType, ResistFireKey, 2.5f), rwLock);
            registry.AddEnchantment(Entry(GeliditesGift, ColdVulnCat, 350, VulnType, ResistColdKey, 2.85f), rwLock);
            registry.AddEnchantment(Entry(ImperilOther, ImperilCat, 350, ImperilType, 0, -225.0f), rwLock);
            registry.AddEnchantment(Entry(4291, ArmorSelfCat, 400, ArmorSelfIncantationType, 0, 250.0f), rwLock);
            registry.AddEnchantment(Entry(WeaknessOtherVI, WeaknessCat, 300, AttributeType, StrengthKey, -35.0f), rwLock);
            target.EnchantmentManager.InvalidateCaches();

            return Creature.BuildVulnerabilityProfile(target);
        }

        [TestMethod]
        public void BuildVulnerabilityProfile_ReadsTheNegativeOnlyBodyArmor()
        {
            // Imperil IV (-100) under Armor Self VIII (+250): the NET mod is +150, which used to read as
            // un-imperiled and promote Imperil to Preferred
            var target = TestCreatures.CreateDefender();
            target.Biota.PropertiesEnchantmentRegistry.AddEnchantment(Entry(1325, ImperilCat, 200, ImperilType, 0, -100.0f), target.BiotaDatabaseLock);
            target.Biota.PropertiesEnchantmentRegistry.AddEnchantment(Entry(4291, ArmorSelfCat, 400, ArmorSelfIncantationType, 0, 250.0f), target.BiotaDatabaseLock);
            target.EnchantmentManager.InvalidateCaches();

            var profile = Creature.BuildVulnerabilityProfile(target);

            Assert.AreEqual(-100, profile.NegativeBodyArmorMod);
            Assert.AreEqual(MonsterSpellPriority.Neutral, MonsterSpellSelector.Classify(ShapeOf(ImperilOther), profile),
                "a standing Imperil under Armor Self must not read as absent");
        }

        [TestMethod]
        public void Simulation_BuffedAndFullyDebuffedTarget_GetsNearZeroRepeatDebuffs()
        {
            // discriminating test (1): the owner report. Every debuff in the book is already on the player, who
            // also runs Armor Self VIII.
            var profile = BuffedAndFullyDebuffedTarget();
            var picks = Simulate(ThreadCasterBook(), profile, seed: 20261006);

            var repeats = DebuffIds.Sum(id => picks.TryGetValue(id, out var n) ? n : 0);
            var repeatRate = (double)repeats / Trials;

            Console.WriteLine($"repeat-debuff picks: {repeats} / {Trials} = {repeatRate:P2}; " +
                string.Join(", ", picks.OrderBy(p => p.Key).Select(p => $"{p.Key}={(double)p.Value / Trials:P2}")));

            Assert.IsTrue(repeatRate < 0.001, $"repeat-debuff rate {repeatRate:P2} must be near zero");
        }

        [TestMethod]
        public void Simulation_CastRateMatchesTheOriginalRoll()
        {
            // discriminating test (2): skipping the Redundant entries must not change P(cast at all), which for
            // the original roll is 1 - prod(1 - p_i) over the WHOLE book
            foreach (var (name, profile) in new[] { ("clean", Clean), ("buffed+debuffed", BuffedAndFullyDebuffedTarget()), ("fire vuln only", WithFireVuln(2.5f)) })
            {
                var book = ThreadCasterBook();
                var expected = 1.0 - book.Values.Aggregate(1.0, (acc, v) => acc * (1.0 - MonsterSpellSelector.ToProbability(v)));

                var picks = Simulate(book, profile, seed: 7);
                var observed = 1.0 - (double)(picks.TryGetValue(0, out var none) ? none : 0) / Trials;

                var sigma = Math.Sqrt(expected * (1.0 - expected) / Trials);

                Console.WriteLine($"{name}: P(cast) expected {expected:P3}, observed {observed:P3}, sigma {sigma:P3}");

                Assert.AreEqual(expected, observed, 4.0 * sigma, $"{name}: cast rate drifted from the original roll");
            }
        }

        [TestMethod]
        public void Simulation_AttributeDebuff_SkippedWhenCarried_RolledWhenAbsent()
        {
            // discriminating test (3)
            var book = new Dictionary<int, float> { { WeaknessOtherVI, 2.10f }, { HarmOtherVI, 2.10f } };

            var carried = Simulate(book, Profile(CarriedWeakness(-35.0f)), seed: 3);
            var absent = Simulate(book, Clean, seed: 3);

            Assert.IsFalse(carried.ContainsKey(WeaknessOtherVI), "a carried Weakness must never be re-cast");
            Assert.IsTrue(absent.TryGetValue(WeaknessOtherVI, out var n) && n > Trials / 20, "an absent Weakness must still be cast");
        }
    }
}
