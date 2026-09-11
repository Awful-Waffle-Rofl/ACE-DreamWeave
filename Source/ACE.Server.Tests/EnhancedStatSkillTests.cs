using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers the generated "Enhanced X" passive stat class ability family: that it spans exactly the
    /// intended skills/attributes/vitals, its tier bonuses match the +10/+25/+50 spec, and its ids
    /// round-trip through the registry. The actual stat injection (into CreatureSkill/Attribute/Vital
    /// Base) needs a live Player and is exercised in-game.
    /// </summary>
    [TestClass]
    public class EnhancedStatSkillTests
    {
        [TestMethod]
        public void TierBonus_MatchesSpec_AndClamps()
        {
            Assert.AreEqual(0, EnhancedStatAbility.BonusForRank(0));
            Assert.AreEqual(10, EnhancedStatAbility.BonusForRank(1));
            Assert.AreEqual(25, EnhancedStatAbility.BonusForRank(2));
            Assert.AreEqual(50, EnhancedStatAbility.BonusForRank(3));

            // out-of-range ranks clamp rather than throw
            Assert.AreEqual(50, EnhancedStatAbility.BonusForRank(99));
            Assert.AreEqual(0, EnhancedStatAbility.BonusForRank(-5));
        }

        [TestMethod]
        public void GeneratesOnePerValidSkillPlusAttributesAndVitals()
        {
            var generated = EnhancedStatAbility.GenerateAll().ToList();

            var skillCount = generated.Count(s => s.Kind == EnhancedStatKind.Skill);
            var attrCount = generated.Count(s => s.Kind == EnhancedStatKind.Attribute);
            var vitalCount = generated.Count(s => s.Kind == EnhancedStatKind.Vital);

            Assert.AreEqual(SkillHelper.ValidSkills.Count, skillCount, "one Enhanced skill per valid player skill");
            Assert.AreEqual(6, attrCount, "one Enhanced attribute per primary attribute");
            Assert.AreEqual(3, vitalCount, "one Enhanced vital per secondary attribute");

            foreach (var enhanced in generated)
            {
                Assert.IsTrue(enhanced.Definition.Implemented, $"{enhanced.Definition.Name}: should be Implemented");
                Assert.AreEqual(EnhancedStatAbility.MaxTier, enhanced.Definition.MaxRank);
                Assert.AreEqual(EnhancedStatAbility.MaxTier, enhanced.Definition.CostPerRank.Length);
                Assert.IsTrue(enhanced.Definition.Name.StartsWith("enhanced_"));
                Assert.IsTrue(enhanced.Definition.Category.StartsWith("Enhanced "));
            }
        }

        [TestMethod]
        public void ClassIds_AreUnique_AndDisjointFromHandWritten()
        {
            var generated = EnhancedStatAbility.GenerateAll().ToList();
            var ids = generated.Select(s => s.Definition.Id).ToList();

            Assert.AreEqual(ids.Count, ids.Distinct().Count(), "generated ids must be unique");

            // hand-authored ids are the small named ClassAbilityId values; generated ids live in high ranges
            var handWritten = ClassAbilityRegistry.Handlers
                .Where(h => h is not IPassiveStatAbility)
                .Select(h => h.Definition.Id)
                .ToHashSet();

            Assert.IsFalse(ids.Any(handWritten.Contains), "generated ids must not collide with hand-written skills");
        }

        [TestMethod]
        public void KnownTargets_MapToExpectedTokensAndIds()
        {
            var generated = EnhancedStatAbility.GenerateAll().ToList();

            var focus = generated.Single(s => s.Kind == EnhancedStatKind.Attribute && s.TargetAttribute == PropertyAttribute.Focus);
            Assert.AreEqual("enhanced_focus", focus.Definition.Name);
            Assert.AreEqual("Enhanced Focus", focus.Definition.DisplayName);
            Assert.AreEqual(EnhancedStatAbility.ClassIdForAttribute(PropertyAttribute.Focus), focus.Definition.Id);

            // "Will" in the request maps to the Self attribute (the game's name)
            var self = generated.Single(s => s.Kind == EnhancedStatKind.Attribute && s.TargetAttribute == PropertyAttribute.Self);
            Assert.AreEqual("enhanced_self", self.Definition.Name);

            var health = generated.Single(s => s.Kind == EnhancedStatKind.Vital && s.TargetVital == PropertyAttribute2nd.MaxHealth);
            Assert.AreEqual("enhanced_health", health.Definition.Name);
            Assert.AreEqual("Enhanced Health", health.Definition.DisplayName);

            var heavy = generated.Single(s => s.Kind == EnhancedStatKind.Skill && s.TargetSkill == Skill.HeavyWeapons);
            Assert.AreEqual("enhanced_heavyweapons", heavy.Definition.Name);
            Assert.AreEqual("Enhanced Heavy Weapons", heavy.Definition.DisplayName);
        }

        [TestMethod]
        public void RegistryExposesTheFamily_ByNameAndQuestKey()
        {
            // these resolve only because ClassAbilityRegistry merged the generated family into its lookups
            Assert.IsTrue(ClassAbilityRegistry.TryGetByName("enhanced_focus", out var focus));
            Assert.AreEqual(EnhancedStatAbility.ClassIdForAttribute(PropertyAttribute.Focus), focus.Id);

            Assert.IsTrue(ClassAbilityRegistry.TryGetByQuestKey(ClassAbilityRegistry.QuestKey(focus), out var byKey));
            Assert.AreEqual(focus.Id, byKey.Id);

            Assert.IsTrue(ClassAbilityRegistry.TryGetByName("enhanced_health", out _));
            Assert.IsTrue(ClassAbilityRegistry.TryGetByName("enhanced_lifemagic", out _));
        }

        // ---- GetReadout label selection + prefix ------------------------------------------------------
        // GetReadout doesn't touch its Player argument (the bonus comes from BonusForRank alone), so these
        // can call it with a null player the same way SpellswordReadoutTests does for its gear abilities -
        // no live Player is needed to exercise the pure label-selection switch.

        [TestMethod]
        public void GetReadout_SkillKind_LabelsAsSkills()
        {
            var generated = EnhancedStatAbility.GenerateAll().ToList();
            var heavy = generated.Single(s => s.Kind == EnhancedStatKind.Skill && s.TargetSkill == Skill.HeavyWeapons);

            var readout = heavy.GetReadout(null, 2);

            Assert.AreEqual("skills", readout.Label);
        }

        [TestMethod]
        public void GetReadout_AttributeKind_LabelsAsAttr()
        {
            var generated = EnhancedStatAbility.GenerateAll().ToList();
            var focus = generated.Single(s => s.Kind == EnhancedStatKind.Attribute && s.TargetAttribute == PropertyAttribute.Focus);

            var readout = focus.GetReadout(null, 1);

            Assert.AreEqual("attr", readout.Label);
        }

        [TestMethod]
        public void GetReadout_VitalKind_LabelsAsVital()
        {
            var generated = EnhancedStatAbility.GenerateAll().ToList();
            var health = generated.Single(s => s.Kind == EnhancedStatKind.Vital && s.TargetVital == PropertyAttribute2nd.MaxHealth);

            var readout = health.GetReadout(null, 3);

            Assert.AreEqual("vital", readout.Label);
        }

        [TestMethod]
        public void GetReadout_AlwaysCarriesAPlusPrefix()
        {
            var generated = EnhancedStatAbility.GenerateAll().ToList();
            var heavy = generated.Single(s => s.Kind == EnhancedStatKind.Skill && s.TargetSkill == Skill.HeavyWeapons);

            var readout = heavy.GetReadout(null, 2);

            Assert.AreEqual("+", readout.Prefix);
            Assert.AreEqual(25.0, readout.Effective, 1e-9);
            Assert.AreEqual("", readout.Unit);
        }
    }
}
