using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Hunter's Mark as its own stacking debuff (owner ruling 2026-09-14: "a stackable separate debuff";
    /// strongest mark applies across archers; life damage takes it, Drain does not).
    ///
    /// Covers the four ways the rework can be silently wrong: the stacking rule (HuntersMarkMath), the entry
    /// shape and category (it must not leak into any StatModType aggregator), one-entry-per-caster refresh
    /// semantics, and the composition inside Creature.GetResistanceMod (the mark must MULTIPLY after the
    /// vulnerability-vs-weapon max, on every return path).
    ///
    /// No Player is constructed (that fails in this host's static initializer) and no Spell either; the
    /// target is a TestCreatures creature and the casters are bare GenericObjects, the same shape
    /// ClassAbilityEnchantmentBandTests and SneakAttackPocketSandTests use. HuntersMarkAbility.ApplyMark is
    /// the production write site, so these tests exercise the shipped identity, category, type and key.
    /// </summary>
    [TestClass]
    public class HuntersMarkDebuffTests
    {
        private const float Tolerance = 1e-5f;

        private const string ExpectedDescription =
            "Any weapon attack you land marks the target for 10 seconds. A marked creature takes 1/2/3/4/5% more " +
            "damage (by rank) from every source and damage type, including your fellows and pets. The mark is its " +
            "own debuff: it stacks with Vulnerability, Imperil and rending weapons. If several archers mark a " +
            "target, the strongest mark applies. Higher Assess Creature multiplies the bonus.";

        private static uint nextCasterGuid = 0x70000500;

        private static WorldObject CreateBareWorldObject()
        {
            var biota = new Biota
            {
                Id = nextCasterGuid++,
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
            };

            return new GenericObject(biota);
        }

        private static SpellCategory MarkCategory => (SpellCategory)EnchantmentManager.SpellCategory_ClassAbility_HuntersMark;

        /// <summary>
        /// The expectation for <see cref="HuntersMarkMath.Combine"/>, computed independently of the
        /// implementation and branching on the SAME constant, so flipping StackAdditively changes what these
        /// assertions expect without editing them.
        /// </summary>
        private static float ExpectedCombine(params float[] mods)
        {
            var marks = mods.Where(m => m > 1.0f).ToArray();

            if (marks.Length == 0)
                return 1.0f;

            return HuntersMarkMath.StackAdditively
                ? 1.0f + marks.Sum(m => m - 1.0f)
                : marks.Max();
        }

        /// <summary>
        /// A retail resist-keyed Vulnerability entry, written directly into the registry the way
        /// EnchantmentManager.Add would leave it.
        /// </summary>
        private static void AddRetailPierceVulnerability(Creature creature, float value)
        {
            var entry = new PropertiesEnchantmentRegistry
            {
                SpellId = (int)SpellId.PiercingVulnerabilityOther6,
                SpellCategory = SpellCategory.PierceVulnerability,
                LayerId = 1,
                PowerLevel = 250,
                StartTime = 0,
                Duration = 60.0,
                StatModType = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative,
                StatModKey = (uint)PropertyFloat.ResistPierce,
                StatModValue = value,
                EnchantmentCategory = (uint)SpellType.Enchantment,
            };

            creature.Biota.PropertiesEnchantmentRegistry.AddEnchantment(entry, creature.BiotaDatabaseLock);
        }

        // ---- 1. HuntersMarkMath ---------------------------------------------------------------------

        [TestMethod]
        public void StackAdditively_IsFalse_ByOwnerRuling20260914()
        {
            // The owner ruled strongest-applies. Flipping the constant must be a deliberate, visible change.
            Assert.AreEqual(false, HuntersMarkMath.StackAdditively);
        }

        [TestMethod]
        public void Combine_EmptyOrNull_IsOne()
        {
            Assert.AreEqual(1.0f, HuntersMarkMath.Combine(new float[0]), Tolerance);
            Assert.AreEqual(1.0f, HuntersMarkMath.Combine(null), Tolerance);
        }

        [TestMethod]
        public void Combine_Single_IsThatMark()
        {
            Assert.AreEqual(ExpectedCombine(1.03f), HuntersMarkMath.Combine(new[] { 1.03f }), Tolerance);
            Assert.AreEqual(1.03f, HuntersMarkMath.Combine(new[] { 1.03f }), Tolerance);
        }

        [TestMethod]
        public void Combine_Several_FollowsTheStackingConstant()
        {
            var mods = new[] { 1.02f, 1.05f, 1.03f };
            Assert.AreEqual(ExpectedCombine(mods), HuntersMarkMath.Combine(mods), Tolerance);
        }

        [TestMethod]
        public void Combine_IgnoresValuesAtOrBelowOne()
        {
            var mods = new[] { 1.0f, 0.5f, 1.04f, 0.0f };
            Assert.AreEqual(ExpectedCombine(mods), HuntersMarkMath.Combine(mods), Tolerance);
            Assert.AreEqual(1.0f, HuntersMarkMath.Combine(new[] { 1.0f, 0.9f }), Tolerance);
        }

        [TestMethod]
        public void CombineStrongest_IsTheMax()
        {
            Assert.AreEqual(1.05f, HuntersMarkMath.CombineStrongest(new[] { 1.02f, 1.05f, 1.03f }), Tolerance);
            Assert.AreEqual(1.0f, HuntersMarkMath.CombineStrongest(new[] { 0.8f }), Tolerance);
        }

        [TestMethod]
        public void CombineAdditive_SumsTheExcess()
        {
            // two +3% marks and a +5% mark: +11%, not the product and not the max
            Assert.AreEqual(1.11f, HuntersMarkMath.CombineAdditive(new[] { 1.03f, 1.03f, 1.05f }), Tolerance);
            Assert.AreEqual(1.02f, HuntersMarkMath.CombineAdditive(new[] { 1.02f, 0.5f }), Tolerance);
            Assert.AreEqual(1.0f, HuntersMarkMath.CombineAdditive(null), Tolerance);
        }

        // ---- 2. Category ---------------------------------------------------------------------------

        [TestMethod]
        public void HuntersMarkCategory_IsSlotTwoOfTheBand_DistinctAndInRange()
        {
            Assert.AreEqual(EnchantmentManager.SpellCategory_ClassAbility_Base + 2,
                EnchantmentManager.SpellCategory_ClassAbility_HuntersMark, "Hunter's Mark holds slot 2 of the band");

            Assert.AreNotEqual(EnchantmentManager.SpellCategory_ClassAbility_PocketSand,
                EnchantmentManager.SpellCategory_ClassAbility_HuntersMark, "sharing Pocket Sand's slot would merge the two");

            Assert.IsTrue(EnchantmentManager.SpellCategory_ClassAbility_HuntersMark > 733, "clear of every real SpellCategory");
            Assert.IsTrue(EnchantmentManager.SpellCategory_ClassAbility_HuntersMark < EnchantmentManager.SpellCategory_Cooldown, "clear of the cooldown band");

            var realCategory = Enum.GetValues(typeof(SpellCategory)).Cast<object>()
                .Any(v => Convert.ToInt32(v) == EnchantmentManager.SpellCategory_ClassAbility_HuntersMark);
            Assert.IsFalse(realCategory, "must not collide with a defined SpellCategory member");
        }

        // ---- entry shape ---------------------------------------------------------------------------

        [TestMethod]
        public void ApplyMark_WritesTheFixedIdentityCategoryTypeAndKey()
        {
            var target = TestCreatures.CreateQuestBearer();
            var caster = CreateBareWorldObject();

            var entry = HuntersMarkAbility.ApplyMark(target, caster, 0.04, 10.0);

            Assert.IsNotNull(entry);
            Assert.AreEqual((int)SpellId.GauntletVulnerabilitySelf, entry.SpellId, "one fixed, uncastable identity spell id");
            Assert.AreEqual(6324, entry.SpellId, "Gauntlet Vulnerability Self");
            Assert.AreEqual((int)HuntersMarkAbility.MarkIdentitySpell, entry.SpellId);
            Assert.AreEqual(MarkCategory, entry.SpellCategory);
            Assert.AreEqual(EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat, entry.StatModType);
            Assert.AreEqual(0u, entry.StatModKey);
            Assert.AreEqual(1.04f, entry.StatModValue, Tolerance);
            Assert.AreEqual(caster.Guid.Full, entry.CasterObjectId);
            Assert.AreEqual(10.0, entry.Duration, 1e-9);

            Assert.IsFalse(entry.StatModType.HasFlag(EnchantmentTypeFlags.Beneficial), "a debuff must not be flagged Beneficial");
            Assert.AreNotEqual(EnchantmentTypeFlags.Undef, entry.StatModType, "Undef would be ambiguous with the match-everything filter");
        }

        [TestMethod]
        public void ApplyMark_NonPositivePercentOrDuration_WritesNothing()
        {
            var target = TestCreatures.CreateQuestBearer();
            var caster = CreateBareWorldObject();

            Assert.IsNull(HuntersMarkAbility.ApplyMark(target, caster, 0.0, 10.0));
            Assert.IsNull(HuntersMarkAbility.ApplyMark(target, caster, 0.05, 0.0));
            Assert.AreEqual(0, target.Biota.PropertiesEnchantmentRegistry.Count);
        }

        /// <summary>
        /// The mark's StatModType/Key must not reach any aggregator. The positive control (the mark IS present
        /// and GetHuntersMarkMod DOES read it) keeps this from passing vacuously.
        /// </summary>
        [TestMethod]
        public void MarkEntry_IsInvisibleToEveryStatModAggregator()
        {
            var target = TestCreatures.CreateQuestBearer();
            var em = target.EnchantmentManager;

            HuntersMarkAbility.ApplyMark(target, CreateBareWorldObject(), 0.05, 10.0);

            Assert.AreEqual(1, target.Biota.PropertiesEnchantmentRegistry.Count, "control: the mark was written");
            Assert.AreEqual(1.05f, target.GetHuntersMarkMod(), Tolerance, "control: the mark is read by its own getter");

            foreach (var type in new[] { DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon, DamageType.Fire,
                DamageType.Cold, DamageType.Acid, DamageType.Electric, DamageType.Nether })
            {
                Assert.AreEqual(1.0f, em.GetVulnerabilityResistanceMod(type), Tolerance, $"vulnerability {type}");
                Assert.AreEqual(1.0f, em.GetProtectionResistanceMod(type), Tolerance, $"protection {type}");
                Assert.AreEqual(1.0f, em.GetResistanceMod(type), Tolerance, $"resistance {type}");
                Assert.AreEqual(0.0f, em.GetArmorModVsType(type), Tolerance, $"bane {type}");
            }

            Assert.AreEqual(0, em.GetBodyArmorMod());
            Assert.AreEqual(0, em.GetSkillMod_Additives(Skill.HeavyWeapons));
            Assert.AreEqual(1.0f, em.GetSkillMod_Multiplier(Skill.HeavyWeapons), Tolerance);
            Assert.AreEqual(0, em.GetAttributeMod_Additive(PropertyAttribute.Strength));
            Assert.AreEqual(1.0f, em.GetAttributeMod_Multiplier(PropertyAttribute.Strength), Tolerance);
            Assert.AreEqual(0, em.GetAttackDebuffMod());
            Assert.AreEqual(0, em.GetDefenseDebuffMod());
            Assert.AreEqual(0, em.GetRating(PropertyInt.DamageRating));
            Assert.AreEqual(0.0f, em.GetDamageOverTimeMod(), Tolerance);
            Assert.AreEqual(0.0f, em.GetDamageMod(), Tolerance);
            Assert.AreEqual(0, em.GetEnchantments_TopLayer(EnchantmentTypeFlags.Beneficial).Count, "not dispellable as a buff");
        }

        // ---- 3. GetHuntersMarkMod before/after -----------------------------------------------------

        [TestMethod]
        public void GetHuntersMarkMod_IsOneBeforeAnyMark_AndReadsTheMarkAfter()
        {
            var target = TestCreatures.CreateQuestBearer();

            // read first: primes any cache, so a stale read after the add would show up here
            Assert.AreEqual(1.0f, target.GetHuntersMarkMod(), Tolerance);
            Assert.AreEqual(1.0f, target.GetResistanceMod(DamageType.Slash, null, null), Tolerance);

            HuntersMarkAbility.ApplyMark(target, CreateBareWorldObject(), 0.03, 10.0);

            Assert.AreEqual(1.03f, target.GetHuntersMarkMod(), Tolerance);
            Assert.AreEqual(1.03f, target.GetResistanceMod(DamageType.Slash, null, null), Tolerance);
        }

        // ---- 4. two casters -----------------------------------------------------------------------

        [TestMethod]
        public void TwoCasters_CombineByTheStackingRule_NotBySum()
        {
            var target = TestCreatures.CreateQuestBearer();

            HuntersMarkAbility.ApplyMark(target, CreateBareWorldObject(), 0.02, 10.0);
            HuntersMarkAbility.ApplyMark(target, CreateBareWorldObject(), 0.05, 10.0);

            Assert.AreEqual(2, target.EnchantmentManager.GetEnchantments(MarkCategory).Count, "one entry per caster");
            Assert.AreEqual(ExpectedCombine(1.02f, 1.05f), target.GetHuntersMarkMod(), Tolerance);
            Assert.AreEqual(1.05f, target.GetHuntersMarkMod(), Tolerance, "strongest applies (owner ruling 2026-09-14)");
        }

        // ---- 5. same caster refresh ---------------------------------------------------------------

        [TestMethod]
        public void SameCaster_ReapplyingLower_OverwritesItsOwnEntry()
        {
            var target = TestCreatures.CreateQuestBearer();
            var caster = CreateBareWorldObject();

            var first = HuntersMarkAbility.ApplyMark(target, caster, 0.05, 10.0);
            first.StartTime = -8.0;   // as the heartbeat would have ticked it

            var second = HuntersMarkAbility.ApplyMark(target, caster, 0.02, 10.0);

            Assert.AreSame(first, second, "the caster's own entry is refreshed, not layered");
            Assert.AreEqual(1, target.Biota.PropertiesEnchantmentRegistry.Count);
            Assert.AreEqual(0.0, second.StartTime, 1e-9, "clock reset");
            Assert.AreEqual(1.02f, second.StatModValue, Tolerance, "magnitude overwritten, not frozen at the first roll");
            Assert.AreEqual(1.02f, target.GetHuntersMarkMod(), Tolerance);
        }

        // ---- 5b. visual plays only on the unmarked -> marked transition (owner ruling 2026-09-25) -----

        [TestMethod]
        public void Visual_IsEnchantDownGreen_NotImperilsShieldDownGrey()
        {
            Assert.AreEqual(PlayScript.EnchantDownGreen, HuntersMarkAbility.MarkVisual);
        }

        [TestMethod]
        public void Visual_FirstMarkPlays_SameCasterRenewalDoesNot()
        {
            var target = TestCreatures.CreateQuestBearer();
            var caster = CreateBareWorldObject();

            Assert.IsFalse(HuntersMarkAbility.IsMarked(target));
            Assert.AreEqual(PlayScript.EnchantDownGreen, HuntersMarkAbility.ApplyMarkAndGetVisual(target, caster, 0.05, 10.0));
            Assert.IsTrue(HuntersMarkAbility.IsMarked(target));

            Assert.IsNull(HuntersMarkAbility.ApplyMarkAndGetVisual(target, caster, 0.05, 10.0), "renewal replays nothing");
            Assert.IsNull(HuntersMarkAbility.ApplyMarkAndGetVisual(target, caster, 0.02, 10.0), "a lower-rank renewal replays nothing");
            Assert.AreEqual(1, target.EnchantmentManager.GetEnchantments(MarkCategory).Count, "the renewals still wrote the mark");
        }

        [TestMethod]
        public void Visual_SecondArcherOnAMarkedTarget_DoesNotPlay()
        {
            var target = TestCreatures.CreateQuestBearer();

            Assert.IsNotNull(HuntersMarkAbility.ApplyMarkAndGetVisual(target, CreateBareWorldObject(), 0.02, 10.0));
            Assert.IsNull(HuntersMarkAbility.ApplyMarkAndGetVisual(target, CreateBareWorldObject(), 0.05, 10.0));
            Assert.AreEqual(2, target.EnchantmentManager.GetEnchantments(MarkCategory).Count, "the second archer's mark was still written");
        }

        [TestMethod]
        public void Visual_PlaysAgainAfterTheMarkIsRemoved()
        {
            var target = TestCreatures.CreateQuestBearer();
            var caster = CreateBareWorldObject();

            var entry = HuntersMarkAbility.ApplyMark(target, caster, 0.05, 10.0);
            target.EnchantmentManager.Remove(entry);   // as the heartbeat does on expiry

            Assert.IsFalse(HuntersMarkAbility.IsMarked(target));
            Assert.AreEqual(PlayScript.EnchantDownGreen, HuntersMarkAbility.ApplyMarkAndGetVisual(target, caster, 0.05, 10.0));
        }

        [TestMethod]
        public void Visual_RejectedMarkPlaysNothing_AndLeavesTargetUnmarked()
        {
            var target = TestCreatures.CreateQuestBearer();

            Assert.IsNull(HuntersMarkAbility.ApplyMarkAndGetVisual(target, CreateBareWorldObject(), 0.0, 10.0));
            Assert.IsFalse(HuntersMarkAbility.IsMarked(target));
        }

        [TestMethod]
        public void LowRankCaster_CannotClobberAHighRankCastersMark()
        {
            var target = TestCreatures.CreateQuestBearer();
            var high = CreateBareWorldObject();
            var low = CreateBareWorldObject();

            var highEntry = HuntersMarkAbility.ApplyMark(target, high, 0.05, 10.0);
            HuntersMarkAbility.ApplyMark(target, low, 0.01, 10.0);
            HuntersMarkAbility.ApplyMark(target, low, 0.01, 10.0);

            var entries = target.EnchantmentManager.GetEnchantments(MarkCategory);
            Assert.AreEqual(2, entries.Count);
            Assert.AreEqual(1, entries.Count(e => e.CasterObjectId == high.Guid.Full));
            Assert.AreEqual(1, entries.Count(e => e.CasterObjectId == low.Guid.Full));
            Assert.AreEqual(1.05f, highEntry.StatModValue, Tolerance);
            Assert.AreEqual(1.05f, target.GetHuntersMarkMod(), Tolerance);

            // distinct layers for the shared identity spell id, so the shard's unique index can hold both
            Assert.AreEqual(entries.Count, entries.Select(e => e.LayerId).Distinct().Count());
        }

        [TestMethod]
        public void RemovingOneCastersMark_LeavesTheOther()
        {
            var target = TestCreatures.CreateQuestBearer();
            var high = CreateBareWorldObject();
            var low = CreateBareWorldObject();

            var highEntry = HuntersMarkAbility.ApplyMark(target, high, 0.05, 10.0);
            HuntersMarkAbility.ApplyMark(target, low, 0.02, 10.0);

            target.EnchantmentManager.Remove(highEntry);

            Assert.AreEqual(1, target.EnchantmentManager.GetEnchantments(MarkCategory).Count);
            Assert.AreEqual(1.02f, target.GetHuntersMarkMod(), Tolerance);
        }

        /// <summary>
        /// Code-review regression. EnchantmentManager.Remove deletes the first entry matching (spell id,
        /// caster) and ignores category and layer, so a mark whose identity shared a real castable spell id
        /// deleted the SAME caster's real cast of that spell instead of itself. With a player-castable identity
        /// (Piercing Vulnerability Other I) this fails: the real Vulnerability goes and the mark stays.
        /// </summary>
        [TestMethod]
        public void RemovingTheMark_LeavesTheSameCastersRealPiercingVulnerabilityOtherI()
        {
            var target = TestCreatures.CreateQuestBearer();
            var archer = CreateBareWorldObject();

            // (a) the archer's own real cast of Piercing Vulnerability Other I
            var realVuln = new PropertiesEnchantmentRegistry
            {
                SpellId = (int)SpellId.PiercingVulnerabilityOther1,
                SpellCategory = SpellCategory.PierceVulnerability,
                LayerId = 1,
                PowerLevel = 1,
                CasterObjectId = archer.Guid.Full,
                StartTime = 0,
                Duration = 60.0,
                StatModType = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative,
                StatModKey = (uint)PropertyFloat.ResistPierce,
                StatModValue = 1.1f,
                EnchantmentCategory = (uint)SpellType.Enchantment,
            };
            target.Biota.PropertiesEnchantmentRegistry.AddEnchantment(realVuln, target.BiotaDatabaseLock);

            // (b) the same archer's Hunter's Mark
            var mark = HuntersMarkAbility.ApplyMark(target, archer, 0.05, 10.0);
            Assert.AreEqual(2, target.Biota.PropertiesEnchantmentRegistry.Count, "precondition: both entries present");

            // (c) the mark expires
            target.EnchantmentManager.Remove(mark);

            // (d) only the mark went
            var remaining = target.Biota.PropertiesEnchantmentRegistry.ToList();
            Assert.AreEqual(1, remaining.Count, "exactly one entry removed");
            Assert.AreSame(realVuln, remaining[0], "the real Vulnerability cast must survive the mark's removal");
            Assert.AreEqual(0, target.EnchantmentManager.GetEnchantments(MarkCategory).Count, "the mark itself is gone");
            Assert.AreEqual(1.0f, target.GetHuntersMarkMod(), Tolerance);
        }

        /// <summary>
        /// The identity must not collide with any id another path writes to a creature: the element
        /// Vulnerability ladders (Elemental Rend, Sundermark, DebuffEffect), the other AddClassAbilityDebuff
        /// identities, or anything a player can learn through Player.PlayerSpellTable. PlayerSpellTable is read
        /// from source text because touching Player's statics fails in this test host.
        /// </summary>
        [TestMethod]
        public void MarkIdentity_CollidesWithNoCastableOrClassAbilityIdentity()
        {
            var identity = HuntersMarkAbility.MarkIdentitySpell;

            foreach (var type in new[] { DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon, DamageType.Fire,
                DamageType.Cold, DamageType.Acid, DamageType.Electric })
            {
                for (uint level = 1; level <= 8; level++)
                    Assert.AreNotEqual(identity, ElementalRendAbility.GetVulnerabilitySpell(type, level), $"{type} vulnerability level {level}");
            }

            Assert.AreNotEqual(SpellId.DF_Specialized_AttackDebuff, identity, "Pocket Sand / DebuffEffect attack-skills identity");
            Assert.AreNotEqual(SpellId.ImperilOther4, identity, "DebuffEffect imperil identity");

            var relative = Path.Combine("Source", "ACE.Server", "WorldObjects", "Player_AllowedSpellID.cs");
            string path = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }
            }
            Assert.IsNotNull(path, $"Could not find {relative} by walking up from {AppContext.BaseDirectory}");

            var learnable = Regex.Matches(File.ReadAllText(path), @"\b\d+\b").Select(m => uint.Parse(m.Value)).ToHashSet();
            Assert.IsTrue(learnable.Contains((uint)SpellId.PiercingVulnerabilityOther1), "control: the parse finds a known learnable spell");
            Assert.IsFalse(learnable.Contains((uint)identity), "the identity must not be a player-learnable spell");
        }

        // ---- 6. composition in GetResistanceMod ---------------------------------------------------

        [TestMethod]
        public void Composition_MarkMultipliesAfterTheVulnerabilityVersusWeaponMax()
        {
            var target = TestCreatures.CreateQuestBearer();

            AddRetailPierceVulnerability(target, 1.6f);
            HuntersMarkAbility.ApplyMark(target, CreateBareWorldObject(), 0.05, 10.0);

            // weapon 2.5 beats the 1.6 vulnerability; the mark multiplies on top instead of being discarded
            Assert.AreEqual(Math.Max(1.6f, 2.5f) * 1.05f, target.GetResistanceMod(DamageType.Pierce, null, null, 2.5f), Tolerance);

            // no weapon: the vulnerability wins the max and the mark still multiplies
            Assert.AreEqual(1.6f * 1.05f, target.GetResistanceMod(DamageType.Pierce, null, null), Tolerance);

            // the ResistanceType overload routes through the same path
            Assert.AreEqual(2.5 * 1.05, target.GetResistanceMod(ResistanceType.Pierce, null, null, 2.5f), 1e-4);
        }

        [TestMethod]
        public void Composition_StrongerVulnerabilityDoesNotZeroTheMark()
        {
            var target = TestCreatures.CreateQuestBearer();

            AddRetailPierceVulnerability(target, 3.0f);
            HuntersMarkAbility.ApplyMark(target, CreateBareWorldObject(), 0.05, 10.0);

            Assert.AreEqual(Math.Max(3.0f, 2.5f) * 1.05f, target.GetResistanceMod(DamageType.Pierce, null, null, 2.5f), Tolerance);
        }

        [TestMethod]
        public void Composition_MarkIsElementIndependent_IncludingNether()
        {
            var target = TestCreatures.CreateQuestBearer();

            AddRetailPierceVulnerability(target, 1.6f);
            HuntersMarkAbility.ApplyMark(target, CreateBareWorldObject(), 0.05, 10.0);

            // a different-element hit gets the mark without the pierce vulnerability
            Assert.AreEqual(1.05f, target.GetResistanceMod(DamageType.Fire, null, null), Tolerance);
            Assert.AreEqual(1.05f, target.GetResistanceMod(DamageType.Nether, null, null), Tolerance);
        }

        [TestMethod]
        public void Composition_IgnoreMagicResistEarlyReturn_CarriesTheMark()
        {
            var target = TestCreatures.CreateQuestBearer();
            var hollowWeapon = CreateBareWorldObject();
            hollowWeapon.IgnoreMagicResist = true;

            HuntersMarkAbility.ApplyMark(target, CreateBareWorldObject(), 0.05, 10.0);

            Assert.AreEqual(2.5f * 1.05f, target.GetResistanceMod(DamageType.Slash, null, hollowWeapon, 2.5f), Tolerance);
        }

        // ---- 7. source-count guard ----------------------------------------------------------------

        /// <summary>
        /// Pins the number of GetHuntersMarkMod call sites in Creature_Properties.cs: the hollow-weapon early
        /// return and the final return of GetResistanceMod(DamageType, ...), plus the HealthDrain case of
        /// GetResistanceMod(ResistanceType, ...). The HealthDrain case reads PropertyManager and cannot run in
        /// this host, so this count is its only guard; deleting any one site fails it.
        /// </summary>
        [TestMethod]
        public void CreatureProperties_HasExactlyThreeHuntersMarkCallSites()
        {
            var relative = Path.Combine("Source", "ACE.Server", "WorldObjects", "Creature_Properties.cs");
            string path = null;

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }
            }

            Assert.IsNotNull(path, $"Could not find {relative} by walking up from {AppContext.BaseDirectory}");

            var callSites = File.ReadAllLines(path)
                .Where(line => !line.TrimStart().StartsWith("//"))
                .Sum(line => Regex.Matches(line, @"GetHuntersMarkMod\(\)").Count);

            Assert.AreEqual(3, callSites,
                "GetResistanceMod must multiply the mark on the hollow-weapon early return, the final return, and the HealthDrain case");
        }

        // ---- player-facing text -------------------------------------------------------------------

        [TestMethod]
        public void Description_IsTheRulingText()
        {
            Assert.AreEqual(ExpectedDescription, new HuntersMarkAbility().Definition.Description);
        }
    }
}
