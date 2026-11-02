using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Custom Summons Task 5: the pure decision half of scroll binding. A Spell and a Player cannot be built
    /// in a unit test (the Spell needs the DAT and the world DB, the Player a Session), so the verdict logic
    /// takes plain facts and the Spell/Player overload is a thin extractor over it.
    /// </summary>
    [TestClass]
    public class SummonSpellBindRulesTests
    {
        [TestMethod]
        public void ApplySummonCooldown_WritesRetailCooldownIdAndDuration_ToABlankThatHadNone()
        {
            var weenie = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 998877,
                WeenieType = WeenieType.PetDevice,
                PropertiesBool = new Dictionary<ACE.Entity.Enum.Properties.PropertyBool, bool> { { ACE.Entity.Enum.Properties.PropertyBool.SummonSpellBinder, true } },
                PropertiesString = new Dictionary<ACE.Entity.Enum.Properties.PropertyString, string> { { ACE.Entity.Enum.Properties.PropertyString.Name, "Blank Test Caster" } },
            };

            var device = new ACE.Server.WorldObjects.PetDevice(weenie, new ACE.Entity.ObjectGuid(0x7F0000A1));

            Assert.IsNull(device.CooldownId, "a blank has no SharedCooldown");
            Assert.IsNull(device.CooldownDuration, "a blank has no cooldown duration");

            SummonSpellBindRules.ApplySummonCooldown(device);

            Assert.AreEqual(213, device.CooldownId);
            Assert.AreEqual(45.0, device.CooldownDuration);
        }

        private static SpellBindFacts Plain() => SummonSpellBindRules.FactsFrom(MagicSchool.WarMagic, false, SpellType.Projectile, 0, false);

        [TestMethod]
        public void Allowed_WhenTrainedAndNothingRefusable()
        {
            Assert.AreEqual(BindVerdict.Allowed, SummonSpellBindRules.Classify(Plain(), SkillAdvancementClass.Trained));
            Assert.AreEqual(BindVerdict.Allowed, SummonSpellBindRules.Classify(Plain(), SkillAdvancementClass.Specialized));
        }

        [TestMethod]
        public void ItemTargetingSpell_IsRefused()
        {
            // an item-enchantment spell with no creature category is the one that lands on an item (armor/weapon auras)
            var facts = SummonSpellBindRules.FactsFrom(MagicSchool.ItemEnchantment, false, SpellType.Enchantment, 0, true);

            Assert.AreEqual(BindVerdict.RefusedItemTarget, SummonSpellBindRules.Classify(facts, SkillAdvancementClass.Specialized));
        }

        [TestMethod]
        public void ItemSchoolSpellWithCreatureCategory_IsNotRefusedAsItemTarget()
        {
            // Creature_Magic:154 - HasItemCategory spells land on the caster, not on an item
            var facts = SummonSpellBindRules.FactsFrom(MagicSchool.ItemEnchantment, true, SpellType.Enchantment, 0, true);

            Assert.AreEqual(BindVerdict.Allowed, SummonSpellBindRules.Classify(facts, SkillAdvancementClass.Trained));
        }

        [TestMethod]
        public void PortalSpell_IsRefused()
        {
            foreach (var t in new[] { SpellType.PortalLink, SpellType.PortalRecall, SpellType.PortalSending, SpellType.PortalSummon, SpellType.FellowPortalSending })
            {
                var facts = SummonSpellBindRules.FactsFrom(MagicSchool.CreatureEnchantment, false, t, 0, true);

                Assert.AreEqual(BindVerdict.RefusedPortal, SummonSpellBindRules.Classify(facts, SkillAdvancementClass.Specialized), t.ToString());
            }
        }

        [TestMethod]
        public void SelfDrain_RefusesOnlyTheTwoRuledTransferCases()
        {
            const TransferFlags none = 0;
            var spec = SkillAdvancementClass.Specialized;

            // refused: a Transfer whose destination is the caster itself ("X to Y Self")
            var toSelf = SummonSpellBindRules.FactsFrom(MagicSchool.LifeMagic, false, SpellType.Transfer, TransferFlags.CasterSource | TransferFlags.CasterDestination, true);
            Assert.AreEqual(BindVerdict.RefusedSelfDrain, SummonSpellBindRules.Classify(toSelf, spec));

            // refused: a drain landing on the caster
            var drain = SummonSpellBindRules.FactsFrom(MagicSchool.LifeMagic, false, SpellType.Transfer, TransferFlags.TargetSource | TransferFlags.CasterDestination, false);
            Assert.AreEqual(BindVerdict.RefusedSelfDrain, SummonSpellBindRules.Classify(drain, spec));

            // refused: a beneficial Transfer whose source AND destination are both the target (converts the owner's own vitals)
            var convertTarget = SummonSpellBindRules.FactsFrom(MagicSchool.LifeMagic, false, SpellType.Transfer, TransferFlags.TargetSource | TransferFlags.TargetDestination, true);
            Assert.AreEqual(BindVerdict.RefusedSelfDrain, SummonSpellBindRules.Classify(convertTarget, spec));

            // allowed: Infuse-style, the pet pays from its own vitals and the owner receives
            var infuse = SummonSpellBindRules.FactsFrom(MagicSchool.LifeMagic, false, SpellType.Transfer, TransferFlags.CasterSource | TransferFlags.TargetDestination, true);
            Assert.AreEqual(BindVerdict.Allowed, SummonSpellBindRules.Classify(infuse, spec));

            // allowed: a HARMFUL target-to-target transfer is not the beneficial conversion the ruling names
            var harmfulTargetOnly = SummonSpellBindRules.FactsFrom(MagicSchool.LifeMagic, false, SpellType.Transfer, TransferFlags.TargetSource | TransferFlags.TargetDestination, false);
            Assert.AreEqual(BindVerdict.Allowed, SummonSpellBindRules.Classify(harmfulTargetOnly, spec));

            // allowed: a Transfer with no flags, and any non-Transfer (a harmful self-targeted Boost is no longer refused)
            Assert.AreEqual(BindVerdict.Allowed, SummonSpellBindRules.Classify(SummonSpellBindRules.FactsFrom(MagicSchool.LifeMagic, false, SpellType.Transfer, none, true), spec));
            Assert.AreEqual(BindVerdict.Allowed, SummonSpellBindRules.Classify(SummonSpellBindRules.FactsFrom(MagicSchool.LifeMagic, false, SpellType.Boost, none, false), spec));
        }

        [TestMethod]
        public void UntrainedOrInactiveSchool_IsRefusedCannotCast()
        {
            Assert.AreEqual(BindVerdict.RefusedCannotCast, SummonSpellBindRules.Classify(Plain(), SkillAdvancementClass.Untrained));
            Assert.AreEqual(BindVerdict.RefusedCannotCast, SummonSpellBindRules.Classify(Plain(), SkillAdvancementClass.Inactive));
        }

        [TestMethod]
        public void RefusalOrder_ItemThenPortalThenDrainThenSkill()
        {
            var all = new SpellBindFacts(true, true, true);
            Assert.AreEqual(BindVerdict.RefusedItemTarget, SummonSpellBindRules.Classify(all, SkillAdvancementClass.Untrained));
            Assert.AreEqual(BindVerdict.RefusedPortal, SummonSpellBindRules.Classify(new SpellBindFacts(false, true, true), SkillAdvancementClass.Untrained));
            Assert.AreEqual(BindVerdict.RefusedSelfDrain, SummonSpellBindRules.Classify(new SpellBindFacts(false, false, true), SkillAdvancementClass.Untrained));
        }

        // ---- third scroll ---------------------------------------------------------------------------------

        [TestMethod]
        public void ThirdScroll_Refused()
        {
            Assert.AreEqual(BindVerdict.RefusedFull, SummonSpellBindRules.Classify(2, Plain(), SkillAdvancementClass.Specialized));
            Assert.AreEqual(BindVerdict.Allowed, SummonSpellBindRules.Classify(1, Plain(), SkillAdvancementClass.Specialized));
            Assert.AreEqual(BindVerdict.Allowed, SummonSpellBindRules.Classify(0, Plain(), SkillAdvancementClass.Specialized));
        }

        [TestMethod]
        public void NextSlot_FillsOneThenTwoThenReportsFull()
        {
            Assert.AreEqual(1, SummonSpellBindRules.NextSlot(null, null));
            Assert.AreEqual(2, SummonSpellBindRules.NextSlot(2076u, null));
            Assert.AreEqual(0, SummonSpellBindRules.NextSlot(2076u, 2101u));
            Assert.AreEqual(0, SummonSpellBindRules.BoundCount(null, null));
            Assert.AreEqual(2, SummonSpellBindRules.BoundCount(1u, 2u));
        }

        // ---- every refusal leaves scroll and blank untouched ---------------------------------------------

        [TestMethod]
        public void EveryRefusal_ConsumesNothing_AndLeavesTheBlankUnchanged()
        {
            foreach (var v in new[] { BindVerdict.RefusedItemTarget, BindVerdict.RefusedPortal, BindVerdict.RefusedSelfDrain, BindVerdict.RefusedCannotCast, BindVerdict.RefusedFull })
            {
                var r = SummonSpellBindRules.Apply(2076u, null, 5000u, v);

                Assert.IsFalse(r.ConsumeScroll, v.ToString());
                Assert.AreEqual(2076u, r.Spell1, v.ToString());
                Assert.IsNull(r.Spell2, v.ToString());
                Assert.IsFalse(r.Completed, v.ToString());
            }
        }

        [TestMethod]
        public void Allowed_FirstScrollFillsSlot1_SecondCompletes()
        {
            var first = SummonSpellBindRules.Apply(null, null, 111u, BindVerdict.Allowed);
            Assert.IsTrue(first.ConsumeScroll);
            Assert.AreEqual(111u, first.Spell1);
            Assert.IsNull(first.Spell2);
            Assert.IsFalse(first.Completed);

            var second = SummonSpellBindRules.Apply(first.Spell1, first.Spell2, 222u, BindVerdict.Allowed);
            Assert.IsTrue(second.ConsumeScroll);
            Assert.AreEqual(111u, second.Spell1);
            Assert.AreEqual(222u, second.Spell2);
            Assert.IsTrue(second.Completed);
        }

        [TestMethod]
        public void Messages_NameTheReason()
        {
            StringAssert.Contains(SummonSpellBindRules.Message(BindVerdict.RefusedItemTarget, "Impenetrability"), "item");
            StringAssert.Contains(SummonSpellBindRules.Message(BindVerdict.RefusedPortal, "Portal Tie"), "portal");
            StringAssert.Contains(SummonSpellBindRules.Message(BindVerdict.RefusedSelfDrain, "Infuse Health"), "vital");
            StringAssert.Contains(SummonSpellBindRules.Message(BindVerdict.RefusedCannotCast, "Fire Bolt"), "trained");
            StringAssert.Contains(SummonSpellBindRules.Message(BindVerdict.RefusedFull, "Fire Bolt"), "two");
        }

        // ---- element --------------------------------------------------------------------------------------

        [TestMethod]
        public void ElementOf_MapsTheFourElementsOnly()
        {
            Assert.AreEqual(UiEffects.Fire, SummonSpellBindRules.ElementOf(true, DamageType.Fire));
            Assert.AreEqual(UiEffects.Frost, SummonSpellBindRules.ElementOf(true, DamageType.Cold));
            Assert.AreEqual(UiEffects.Lightning, SummonSpellBindRules.ElementOf(true, DamageType.Electric));
            Assert.AreEqual(UiEffects.Acid, SummonSpellBindRules.ElementOf(true, DamageType.Acid));
            Assert.AreEqual(UiEffects.Undef, SummonSpellBindRules.ElementOf(true, DamageType.Nether));
            Assert.AreEqual(UiEffects.Undef, SummonSpellBindRules.ElementOf(true, DamageType.Slash));
            Assert.AreEqual(UiEffects.Undef, SummonSpellBindRules.ElementOf(false, DamageType.Fire), "a friendly spell carries no element");
        }

        [TestMethod]
        public void CasterElement_IsTheFirstDamagingSpellsElement()
        {
            var fireThenFrost = new[] { (true, DamageType.Fire), (true, DamageType.Cold) };
            Assert.AreEqual(UiEffects.Fire, SummonSpellBindRules.CasterElement(fireThenFrost));

            var healThenFrost = new[] { (false, DamageType.Undef), (true, DamageType.Cold) };
            Assert.AreEqual(UiEffects.Frost, SummonSpellBindRules.CasterElement(healThenFrost), "a friendly first spell is skipped");

            var netherThenFire = new[] { (true, DamageType.Nether), (true, DamageType.Fire) };
            Assert.AreEqual(UiEffects.Undef, SummonSpellBindRules.CasterElement(netherThenFire), "the first damaging spell is non-elemental, so no element");

            var friendlyOnly = new[] { (false, DamageType.Undef), (false, DamageType.Undef) };
            Assert.AreEqual(UiEffects.Undef, SummonSpellBindRules.CasterElement(friendlyOnly));
        }

        [TestMethod]
        public void WithElement_ReplacesElementBitsAndKeepsTheRest()
        {
            var magical = (int)UiEffects.Magical;

            Assert.AreEqual(magical | (int)UiEffects.Fire, SummonSpellBindRules.WithElement(magical, UiEffects.Fire));
            Assert.AreEqual(magical | (int)UiEffects.Acid, SummonSpellBindRules.WithElement(magical | (int)UiEffects.Fire, UiEffects.Acid), "an old element is replaced, not added to");
            Assert.AreEqual(magical, SummonSpellBindRules.WithElement(magical | (int)UiEffects.Frost, UiEffects.Undef), "no element clears the bits");
            Assert.AreEqual((int)UiEffects.Lightning, SummonSpellBindRules.WithElement(null, UiEffects.Lightning));
        }
    }
}
